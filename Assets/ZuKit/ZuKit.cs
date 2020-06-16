using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
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
            this.dateTime = ZuKit.GenerateTimestamp();
        }
    }


    // パラメータ名 + タグ集
    // リクエスト時に自動的にinfluxdb構文に変形される。
    public struct ZuKey
    {
        public readonly string parameterName;
        public readonly (string, object)[] tagValues;

        public ZuKey(string parameterName, params (string, object)[] tagValues)
        {
            this.parameterName = parameterName;
            this.tagValues = tagValues;
        }
    }

    public class ZuKit : IDisposable
    {
        private static DateTime UNIX_EPOCH = new DateTime(1970, 1, 1, 0, 0, 0, 0);

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
                    state = ZuKitState.Running;

                    // 接続、DB生成
                    _this = new ZuKit(influxdbUrl, dbName, onUpdateValues);
                    break;
                default:
                    Debug.LogError("二度目以降のsetupが行われていて、これは禁止したり意味があるなら何かする。");
                    break;
            }
        }

        public static void Teardown()
        {
            switch (state)
            {
                case ZuKitState.Running:
                    state = ZuKitState.Stopped;
                    _this.Dispose();
                    break;
                default:
                    Debug.LogError("二度目以降のteardownが行われていて、これは禁止したり意味があるなら何かする。 state:" + state);
                    break;
            }
        }

        public static string GenerateTimestamp()
        {
            var now = DateTime.Now.ToUniversalTime();

            // get timespan from unix-epoch.
            var elapsedTimeSpan = now - UNIX_EPOCH;

            // 特定桁数の数値に変換 1592278319219425024 19桁
            return ((long)(elapsedTimeSpan.TotalMilliseconds * 1000000)).ToString();

            // memo rfc3339準拠の日付フォーマット
            // return System.Xml.XmlConvert.ToString(DateTime.Now, System.Xml.XmlDateTimeSerializationMode.Utc);
        }




        private Func<ZuValues> onUpdateValues;
        private string influxdbUrl;
        private string dbName;
        private string writeHeader;

        private ConcurrentQueue<ZuValues> queue = new ConcurrentQueue<ZuValues>();
        private IEnumerator requestEnum;
        private StringBuilder requestBuilder = new StringBuilder();

        // IDisposable用
        private bool disposedValue;


        private ZuKit(string influxdbUrl, string dbName, Func<ZuValues> onUpdateValues)
        {
            this.onUpdateValues = onUpdateValues;
            this.influxdbUrl = influxdbUrl;
            this.dbName = dbName;
            this.writeHeader = influxdbUrl + "/write?db=" + dbName + "&precision=ns";// このZuKitはnsまで見たいのでnsまで見る。基本的には順番。

            // 初期リクエストをセット
            requestEnum = DBCreateRequest(influxdbUrl, dbName);

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

        // 毎フレーム実行される関数。UnityのメインスレッドでLateUpdateの最後に実行される。
        private void OnZuKitUpdate()
        {
            // 毎フレーム実行し、データを引き出す
            var values = onUpdateValues();
            if (values.values != null)
            {
                // データをqへと入れる
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
                requestEnum = PointWriteRequest();
                return;
            }
        }

        /*
            influxDBにDBを作成するリクエスト
            既にDBがあるか、新規に作成された場合成功となる。
        */
        private IEnumerator DBCreateRequest(string influxdbUrl, string dbName)
        {
            var req = UnityWebRequest.Post(influxdbUrl + "/query" + Uri.EscapeUriString("?q=CREATE DATABASE " + dbName), string.Empty);
            req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            req.SendWebRequest();

            // リクエストの終了待ちを行う
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

            // 試しの接続時送り出し デバッグなどで使用すると便利。
            // var s = new StringBuilder();
            // ValuesToInfluxDBString(ref s, new ZuValues((new ZuKey("a"), 1)));
            // Debug.Log("s:" + s);
            // req = new UnityWebRequest(writeHeader);
            // req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(s.ToString()));
            // req.method = UnityWebRequest.kHttpVerbPOST;
            // req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            // req.SendWebRequest();

            // while (!req.isDone)
            // {
            //     yield return null;
            // }

            // foreach (var item in req.GetResponseHeaders())
            // {
            //     Debug.Log("item:" + item.Key + " v:" + item.Value);
            // }
        }

        /**
           influxDBへとポイントデータを書き込む。
           queueに入っているものを全件まとめて一つのリクエストとして送り出す。
        */
        private IEnumerator PointWriteRequest()
        {
            var count = queue.Count;
            requestBuilder.Clear();

            // 今回の取り出しで存在する件数を送り出す
            for (var i = 0; i < count; i++)
            {
                ZuValues vals;
                queue.TryDequeue(out vals);

                // 一回のpull単位でまとまっているデータを同じタイムスタンプで整形する。
                ValuesToInfluxDBString(ref requestBuilder, vals);
            }

            var baseStr = requestBuilder.ToString();
            var rStr = baseStr.Substring(0, baseStr.Length - 1);// 最後の改行を削除、、、

            // 送り出す。
            // TODO: UnityのWebReqを使っているが、送り出せるなら別になんでもいいはず、、、というのはある。LINE出力も試したいが、重たいという需要があればの話。例えば100件溜まったら送る、とかで全然緩和できる。
            var req = new UnityWebRequest(writeHeader);
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(rStr));
            req.method = UnityWebRequest.kHttpVerbPOST;
            req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            req.SendWebRequest();

            while (!req.isDone)
            {
                yield return null;
            }

            // 送信後のレスポンスヘッダチェック
            // foreach (var item in req.GetResponseHeaders())
            // {
            //     Debug.Log("item:" + item.Key + " v:" + item.Value);
            // }
        }

        /*
            ZuValuesに入っているvalueをinfluxDB用の文字列に変換する。
            一つのZuValuesから複数列の文字列が生成され、そのタイムスタンプは同じものになる。
        */
        private void ValuesToInfluxDBString(ref StringBuilder stringBuilder, ZuValues sameTimingValues)
        {
            var dateStr = sameTimingValues.dateTime;
            foreach (var val in sameTimingValues.values)
            {
                var zuVal = val.Item1;
                var paramName = zuVal.parameterName;

                // タグがセットしてある場合、それ用の記法で送り出す。
                if (0 < zuVal.tagValues.Length)
                {
                    var tagValues = zuVal.tagValues.Select(t => t.Item1 + "=" + t.Item2).ToArray();
                    stringBuilder.AppendLine(paramName + "," + string.Join(",", tagValues) + " value=" + val.Item2 + " " + dateStr);
                    continue;
                }

                stringBuilder.AppendLine(paramName + " value=" + val.Item2 + " " + dateStr);
            }
        }

        private void RemoveFromRunLoop()
        {
            var playerLoop = PlayerLoop.GetDefaultPlayerLoop();

            // updateのシステムを取得する
            var updateSystem = playerLoop.subSystemList[6];// postLateUpdate
            var subSystem = new List<PlayerLoopSystem>(updateSystem.subSystemList);

            // 取り除く
            subSystem.RemoveAll(w => w.GetType() == typeof(ZuKit));
            updateSystem.subSystemList = subSystem.ToArray();
            playerLoop.subSystemList[6] = updateSystem;// postLateUpdate

            // セット
            PlayerLoop.SetPlayerLoop(playerLoop);
        }

        /*
            dispose処理
        */
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    RemoveFromRunLoop();
                }

                disposedValue = true;
            }
        }

        // ~ZuKit()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}