﻿using FreeRedis.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FreeRedis
{
    partial class RedisClient
    {
        class SingleInsideAdapter : BaseAdapter
        {
            readonly IRedisSocket _redisSocket;

            public SingleInsideAdapter(RedisClient topOwner, RedisClient owner, string host, bool ssl, TimeSpan connectTimeout, TimeSpan receiveTimeout, TimeSpan sendTimeout, Action<RedisClient> connected)
            {
                UseType = UseType.SingleInside;
                TopOwner = topOwner;
                _redisSocket = new DefaultRedisSocket(host, ssl);
                _redisSocket.Connected += (s, e) => connected?.Invoke(owner);
                _redisSocket.ConnectTimeout = connectTimeout;
                _redisSocket.ReceiveTimeout = receiveTimeout;
                _redisSocket.SendTimeout = sendTimeout;
            }

            public override void Dispose()
            {
                _redisSocket.Dispose();
            }

            public override IRedisSocket GetRedisSocket(CommandPacket cmd)
            {
                return DefaultRedisSocket.CreateTempProxy(_redisSocket, null);
            }
            public override TValue AdapaterCall<TReadTextOrStream, TValue>(CommandPacket cmd, Func<RedisResult, TValue> parse)
            {
                return TopOwner.LogCall(cmd, () =>
                {
                    _redisSocket.Write(cmd);
                    var rt = cmd.Read<TReadTextOrStream>();
                    rt.IsErrorThrow = TopOwner._isThrowRedisSimpleError;
                    return parse(rt);
                });
            }
        }
    }
}