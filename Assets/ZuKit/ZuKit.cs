using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.LowLevel;
using UnityEngine.Networking;

namespace ZuKitCore
{
    // influxdbへのwriteに使うデータ型。
    // 一度に複数のデータを放り込むのに便利なように[]をサポートしている。
    // リクエスト時に自動的にinfluxdb構文に変形される。
    public struct ZuValues
    {
        // ZuKey = パラメータ名 + タグ集
        // object = value
        public readonly (ZuKey, object)[] values;
        public readonly string dateTime;
        public ZuValues(params (ZuKey, object)[] values)
        {
            this.values = values;
            this.dateTime = "" + (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;//DateTime.UnixEpoch();////"" + System.Xml.XmlConvert.ToString(DateTime.Now, System.Xml.XmlDateTimeSerializationMode.Utc);//DateTime.UtcNow.Ticks;//ToString("yyyy-MM-ddTHH\\:mm\\:ssZ");
        }
    }

    // パラメータ名 + タグ集
    // リクエスト時に自動的にinfluxdb構文に変形される。
    public class ZuKey
    {
        public readonly string parameterName;
        public readonly (string, object)[] tagValues;

        public ZuKey(string parameterName, params (string, object)[] tagValues)
        {
            this.parameterName = parameterName;
            this.tagValues = tagValues;
        }
    }

    public class ZuKit
    {
        private enum ZuKitState
        {
            None,
            Running,
            Stopping,
            Stopped,
        }

        private static ZuKit _this;
        private static ZuKitState state = ZuKitState.None;

        public static void Setup(string influxdbUrl, string dbName, Func<ZuValues> onUpdateValues)
        {
            switch (state)
            {
                case ZuKitState.None:
                    // 接続とかDB生成を行う
                    _this = new ZuKit(influxdbUrl, dbName, onUpdateValues);
                    state = ZuKitState.Running;
                    break;
                default:
                    Debug.LogError("二度目以降のsetupが行われていて、これは禁止したり意味があるなら何かする。");
                    break;
            }

            // これ以降はrunning状態で、データをキューしまくって、投げまくる。
        }


        private Func<ZuValues> onUpdateValues;
        private string influxdbUrl;
        private string dbName;
        private string writeHeader;

        private ConcurrentQueue<ZuValues> queue = new ConcurrentQueue<ZuValues>();

        private ZuKit(string influxdbUrl, string dbName, Func<ZuValues> onUpdateValues)
        {
            this.onUpdateValues = onUpdateValues;
            this.influxdbUrl = influxdbUrl;
            this.dbName = dbName;
            this.writeHeader = influxdbUrl + "/write?db=" + dbName;

            // 初期リクエストをセット
            requestEnum = ConnectionRequest(influxdbUrl, dbName);

            // メインスレッドで動作する機構をセットする。lateUpdateの後に動作する。
            {
                var zuKitSendSystem = new PlayerLoopSystem()
                {
                    type = typeof(ZuKit),
                    updateDelegate = this.OnZuKitUpdate
                };

                var playerLoop = PlayerLoop.GetDefaultPlayerLoop();

                // updateのシステムを取得する
                var updateSystem = playerLoop.subSystemList[6];// postLateUpdate
                var subSystem = new List<PlayerLoopSystem>(updateSystem.subSystemList);

                // 送信用の処理を末尾にセットする
                subSystem.Add(zuKitSendSystem);
                updateSystem.subSystemList = subSystem.ToArray();
                playerLoop.subSystemList[6] = updateSystem;// postLateUpdate

                // セット
                PlayerLoop.SetPlayerLoop(playerLoop);
            }
        }

        private IEnumerator ConnectionRequest(string influxdbUrl, string dbName)
        {
            var req = UnityWebRequest.Post(influxdbUrl + "/query" + Uri.EscapeUriString("?q=CREATE DATABASE " + dbName), string.Empty);
            req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            req.SendWebRequest();

            while (!req.isDone)
            {
                yield return null;
            }

            if (req.isHttpError || req.isNetworkError)
            {
                Debug.LogError("failed to create db.");
                yield break;
            }

            Debug.Log("succeeded to create db or already exists.");
        }

        private IEnumerator requestEnum;
        private StringBuilder stringBuilder = new StringBuilder();
        private void OnZuKitUpdate()
        {
            // 毎フレーム実行し、データを引き出す
            var values = onUpdateValues();
            if (0 < values.values.Length)
            {
                var count = values.values.Length;
                for (var i = 0; i < count; i++)
                {
                    queue.Enqueue(values);
                }
            }

            // 前回の通信が完了していなければ、完了待ちを行う。
            var cont = requestEnum.MoveNext();
            if (cont)
            {
                return;
            }

            // 次のリクエストを行う。
            if (0 < queue.Count)
            {
                requestEnum = ConstructInfluxDBWrite();
            }
        }

        private IEnumerator ConstructInfluxDBWrite()
        {
            var count = queue.Count;
            ZuValues vals;

            stringBuilder.Clear();
            // 今回の取り出しで存在する件数を送り出す
            for (var i = 0; i < count; i++)
            {
                queue.TryDequeue(out vals);

                // 一回のpull単位でまとまっているデータを同じタイムスタンプで整形する。
                ValuesToInfluxDBString(vals);
            }

            var baseStr = stringBuilder.ToString();
            var rStr = baseStr.Substring(0, baseStr.Length - 1);

            var req = new UnityWebRequest(writeHeader);
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(rStr));
            req.method = UnityWebRequest.kHttpVerbPOST;
            req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            req.SendWebRequest();

            while (!req.isDone)
            {
                yield return null;
            }

            // foreach (var item in req.GetResponseHeaders())
            // {
            //     Debug.Log("item:" + item.Key + " v:" + item.Value);
            // }
        }

        private void ValuesToInfluxDBString(ZuValues sameTimingValues)
        {
            // var utcStr = sameTimingValues.dateTime;
            // Debug.Log("utcStr:" + utcStr);
            foreach (var val in sameTimingValues.values)
            {
                var zuVal = val.Item1;
                var paramName = zuVal.parameterName;

                if (0 < zuVal.tagValues.Length)
                {
                    var tagValues = zuVal.tagValues.Select(t => t.Item1 + "=" + t.Item2).ToArray();
                    stringBuilder.AppendLine(paramName + "," + string.Join(",", tagValues) + " value=" + val.Item2);// + " " + utcStr
                    continue;
                }

                stringBuilder.AppendLine(paramName + " value=" + val.Item2);// + " " + utcStr
            }
        }
    }
}