﻿using FreeRedis.Internal;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace FreeRedis
{
    partial class RedisClient
    {

        protected internal enum UseType {
            Pooling,
            Cluster,
            Sentinel,
            SingleInside, 
            SingleTemp, 
            Pipeline, 
            Transaction,
        }

        protected internal abstract class BaseAdapter
        {
            public static ThreadLocal<Random> _rnd = new ThreadLocal<Random>(() => new Random());
            public UseType UseType { get; protected set; }
            protected internal RedisClient TopOwner { get; internal set; }

            public abstract IRedisSocket GetRedisSocket(CommandPacket cmd);
            public abstract void Dispose();

            public abstract TValue AdapaterCall<TReadTextOrStream, TValue>(CommandPacket cmd, Func<RedisResult, TValue> parse);
        }
    }
}
