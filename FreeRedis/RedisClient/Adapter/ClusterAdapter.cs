﻿using FreeRedis.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FreeRedis
{
    partial class RedisClient
    {
        class ClusterAdapter : BaseAdapter
        {
            readonly IdleBus<RedisClientPool> _ib;

            public ClusterAdapter(RedisClient topOwner, ConnectionStringBuilder[] clusterConnectionStrings)
            {
                UseType = UseType.Cluster;
                TopOwner = topOwner;
                _ib = new IdleBus<RedisClientPool>(TimeSpan.FromMinutes(10));
                _ib.Notice += new EventHandler<IdleBus<string, RedisClientPool>.NoticeEventArgs>((_, e) => { });
            }

            public override void Dispose()
            {
                _ib.Dispose();
            }

            public override IRedisSocket GetRedisSocket(CommandPacket cmd)
            {
                throw new NotImplementedException();
            }
            public override TValue AdapaterCall<TReadTextOrStream, TValue>(CommandPacket cmd, Func<RedisResult, TValue> parse)
            {
                throw new NotImplementedException();
            }

            class ClusterMoved
            {
                public bool ismoved;
                public bool isask;
                public ushort slot;
                public string endpoint;
                public static ClusterMoved ParseSimpleError(string simpleError)
                {
                    if (string.IsNullOrWhiteSpace(simpleError)) return null;
                    var ret = new ClusterMoved
                    {
                        ismoved = simpleError.StartsWith("MOVED "),
                        isask = simpleError.StartsWith("ASK ")
                    };
                    if (ret.ismoved == false && ret.isask == false) return null;
                    var parts = simpleError.Split(new string[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault().Split(new[] { ' ' }, 3);
                    if (parts.Length != 3 ||
                        ushort.TryParse(parts[1], out ret.slot) == false) return null;
                    ret.endpoint = parts[2];
                    return ret;
                }
            }
        }
    }
}
