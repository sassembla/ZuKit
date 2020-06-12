using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        public ZuValues(params (ZuKey, object)[] values)
        {
            this.values = values;
        }
    }

    // パラメータ名 + タグ集
    // リクエスト時に自動的にinfluxdb構文に変形される。
    public class ZuKey
    {
        private string parameterName;
        private (string, object)[] tagValues;

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

        private ConcurrentQueue<ZuValues> queue = new ConcurrentQueue<ZuValues>();

        private ZuKit(string influxdbUrl, string dbName, Func<ZuValues> onUpdateValues)
        {
            Debug.Log("起動、いろいろ用意しよう。 influxdbUrl:" + influxdbUrl + " dbName:" + dbName);
            this.onUpdateValues = onUpdateValues;
            this.influxdbUrl = influxdbUrl;
            this.dbName = dbName;

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
            var request = new Request(influxdbUrl + "/query" + Uri.EscapeUriString("?q=CREATE DATABASE " + dbName));

            var req = UnityWebRequest.Post(request.reqStr, string.Empty);
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


        private bool done = false;
        private IEnumerator requestEnum;
        private StringBuilder stringBuilder = new StringBuilder();
        private void OnZuKitUpdate()
        {
            // 毎フレーム実行し、データを引き出す
            var values = onUpdateValues();
            if (0 < values.values.Length)
            {
                if (!done)
                {
                    done = true;
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
            stringBuilder.Clear();

            stringBuilder.Append(influxdbUrl);
            stringBuilder.Append("/write" + Uri.EscapeUriString("?db=" + dbName));

            var count = queue.Count;
            ZuValues vals;

            string data = string.Empty;
            // 今回の取り出しで存在する件数を送り出す
            for (var i = 0; i < count; i++)
            {
                queue.TryDequeue(out vals);

                // リクエストを合成して投げる、ここが一番頑張るところ
                data = ValuesToInfluxDBString(vals, DateTime.UtcNow);
            }

            var rStr = stringBuilder.ToString();
            Debug.Log("rStr:" + rStr + " data:" + data);

            var req = new UnityWebRequest("http://localhost:8086/write?db=myZuDB");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("key value=0.67\nk2 value=2.0"));
            req.method = UnityWebRequest.kHttpVerbPOST;
            req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            req.SendWebRequest();

            while (!req.isDone)
            {
                yield return null;
            }

            // Debug.Log("req:" + req.isHttpError + " net:" + req.isNetworkError + " err:" + req.error + " code:" + req.responseCode);
            // foreach (var r in req.GetResponseHeaders())
            // {
            //     Debug.Log("k:" + r.Key + " v:" + r.Value);
            // }
        }

        private string ValuesToInfluxDBString(ZuValues sameTimingValues, DateTime utcNow)
        {
            return "key,kind=2 value=0.67";
        }
    }

    public struct Request
    {
        public readonly string reqStr;

        public Request(string v)
        {
            this.reqStr = v;
        }
    }


}