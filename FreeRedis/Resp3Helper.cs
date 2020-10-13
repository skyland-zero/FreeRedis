﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace FreeRedis
{
    public static class Resp3Helper
    {
        public static RedisResult<T> Read<T>(Stream stream) => new Resp3Reader(stream, null).ReadObject().ConvertTo<T>(null);
        public static RedisResult<T> Read<T>(Stream stream, Encoding encoding) => new Resp3Reader(stream, typeof(T) == typeof(byte[]) ? null : encoding).ReadObject().ConvertTo<T>(encoding);
        public static void ReadChunk(Stream stream, Stream destination, int bufferSize = 1024) => new Resp3Reader(stream, null).ReadBlobStringChunk(destination, bufferSize);
        static RedisResult<T> ConvertTo<T>(this RedisResult<object> rt, Encoding encoding)
        {
            var obj = rt.Value;
            if (obj is T val) return rt.NewValue(a => val);
            return rt.NewValue(a => (T)typeof(T).FromObject(a, encoding));
        }

        public static void Write(Stream stream, List<object> command, RedisProtocol protocol) => Write(stream, null, command, protocol);
        public static void Write(Stream stream, Encoding encoding, List<object> command, RedisProtocol protocol) => new Resp3Writer(stream, encoding, protocol).WriteCommand(command);

        public static object DeserializeResptext(string resptext)
        {
            using (var ms = new MemoryStream())
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(resptext);
                    ms.Write(bytes, 0, bytes.Length);
                    ms.Position = 0;
                    return Read<object>(ms, Encoding.UTF8).Value;
                }
                finally
                {
                    ms.Close();
                }
            }
        }
        public static string SerializeResptext(object data, RedisProtocol protocol)
        {
            using (var ms = new MemoryStream())
            {
                try
                {
                    new Resp3Writer(ms, Encoding.UTF8, protocol).WriteObject(data);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
                finally
                {
                    ms.Close();
                }
            }
        }

        class Resp3Reader
        {
            Stream _stream;
            Encoding _encoding;

            public Resp3Reader(Stream stream, Encoding encoding)
            {
                _stream = stream;
                _encoding = encoding;
            }

            public void ReadBlobStringChunk(Stream destination, int bufferSize)
            {
                char c = (char)_stream.ReadByte();
                switch (c)
                {
                    case '$':
                    case '=':
                    case '!': ReadBlobString(c, destination, bufferSize); break;
                    default: throw new ProtocolViolationException($"Expecting fail MessageType '{c}'");
                }
            }

            object ReadBlobString(char msgtype, Stream destination, int bufferSize)
            {
                var clob = ReadClob();
                if (_encoding == null) return clob;
                if (clob == null) return null;
                return _encoding.GetString(clob);

                byte[] ReadClob()
                {
                    MemoryStream ms = null;
                    try
                    {
                        if (destination == null) destination = ms = new MemoryStream();
                        var lenstr = ReadLine(null);
                        if (int.TryParse(lenstr, out var len))
                        {
                            if (len < 0) return null;
                            if (len > 0) Read(destination, len, bufferSize);
                            ReadLine(null);
                            if (len == 0) return new byte[0];
                            return ms?.ToArray();
                        }
                        if (lenstr == "?")
                        {
                            while (true)
                            {
                                char c = (char)_stream.ReadByte();
                                if (c != ';') throw new ProtocolViolationException($"Expecting fail Streamed strings ';', got '{c}'");
                                var clenstr = ReadLine(null);
                                if (int.TryParse(clenstr, out var clen))
                                {
                                    if (clen == 0) break;
                                    if (clen > 0)
                                    {
                                        Read(destination, clen, bufferSize);
                                        ReadLine(null);
                                        continue;
                                    }
                                }
                                throw new ProtocolViolationException($"Expecting fail Streamed strings ';0', got ';{clenstr}'");
                            }
                            return ms?.ToArray();
                        }
                        throw new ProtocolViolationException($"Expecting fail Blob string '{msgtype}0', got '{msgtype}{lenstr}'");
                    }
                    finally
                    {
                        ms?.Close();
                        ms?.Dispose();
                    }
                }
            }
            object ReadSimpleString()
            {
                if (_encoding == null) return ReadClob();
                return _encoding.GetString(ReadClob());

                byte[] ReadClob()
                {
                    MemoryStream ms = null;
                    try
                    {
                        ms = new MemoryStream();
                        ReadLine(ms);
                        return ms.ToArray();
                    }
                    finally
                    {
                        ms?.Close();
                        ms?.Dispose();
                    }
                }
            }
            long ReadNumber(char msgtype)
            {
                var numstr = ReadLine(null);
                if (long.TryParse(numstr, out var num)) return num;
                throw new ProtocolViolationException($"Expecting fail Number '{msgtype}0', got '{msgtype}{numstr}'");
            }
            BigInteger ReadBigNumber(char msgtype)
            {
                var numstr = ReadLine(null);
                if (BigInteger.TryParse(numstr, NumberStyles.Any, null, out var num)) return num;
                throw new ProtocolViolationException($"Expecting fail Number '{msgtype}0', got '{msgtype}{numstr}'");
            }
            double ReadDouble(char msgtype)
            {
                var numstr = ReadLine(null);
                switch (numstr)
                {
                    case "inf": return double.PositiveInfinity;
                    case "-inf": return double.NegativeInfinity;
                }
                if (double.TryParse(numstr, NumberStyles.Any, null, out var num)) return num;
                throw new ProtocolViolationException($"Expecting fail Double '{msgtype}1.23', got '{msgtype}{numstr}'");
            }
            bool ReadBoolean(char msgtype)
            {
                var boolstr = ReadLine(null);
                switch (boolstr)
                {
                    case "t": return true;
                    case "f": return false;
                }
                throw new ProtocolViolationException($"Expecting fail Double '{msgtype}t', got '{msgtype}{boolstr}'");
            }

            List<object> ReadArray(char msgtype)
            {
                var arr = new List<object>();
                var lenstr = ReadLine(null);
                if (int.TryParse(lenstr, out var len))
                {
                    if (len < 0) return null;
                    for (var a = 0; a < len; a++)
                        arr.Add(ReadObject().Value);
                    return arr;
                }
                if (lenstr == "?")
                {
                    while (true)
                    {
                        var ro = ReadObject();
                        if (ro.IsEnd) break;
                        arr.Add(ro.Value);
                    }
                    return arr;
                }
                throw new ProtocolViolationException($"Expecting fail Array '{msgtype}3', got '{msgtype}{lenstr}'");
            }
            List<object> ReadMap(char msgtype)
            {
                var arr = new List<object>();
                var lenstr = ReadLine(null);
                if (int.TryParse(lenstr, out var len))
                {
                    if (len < 0) return null;
                    for (var a = 0; a < len; a++)
                    {
                        arr.Add(ReadObject().Value);
                        arr.Add(ReadObject().Value);
                    }
                    return arr;
                }
                if (lenstr == "?")
                {
                    while (true)
                    {
                        var rokey = ReadObject();
                        var roval = ReadObject();
                        if (roval.IsEnd) break;
                        arr.Add(rokey.Value);
                        arr.Add(roval.Value);
                    }
                    return arr;
                }
                throw new ProtocolViolationException($"Expecting fail Map '{msgtype}3', got '{msgtype}{lenstr}'");
            }

            public RedisResult<object> ReadObject()
            {
                while (true)
                {
                    char c = (char)_stream.ReadByte();
                    switch (c)
                    {
                        case '$': return new RedisResult<object>(ReadBlobString(c, null, 1024), false, RedisMessageType.BlobString);
                        case '+': return new RedisResult<object>(ReadSimpleString(), false, RedisMessageType.SimpleString);
                        case '=': return new RedisResult<object>(ReadBlobString(c, null, 1024), false, RedisMessageType.VerbatimString);
                        case '-': return new RedisResult<object>(ReadSimpleString(), false, RedisMessageType.SimpleError);
                        case '!': return new RedisResult<object>(ReadBlobString(c, null, 1024), false, RedisMessageType.BlobError);
                        case ':': return new RedisResult<object>(ReadNumber(c), false, RedisMessageType.Number);
                        case '(': return new RedisResult<object>(ReadBigNumber(c), false, RedisMessageType.BigNumber);
                        case '_': ReadLine(null); return new RedisResult<object>(null, false, RedisMessageType.Null);
                        case ',': return new RedisResult<object>(ReadDouble(c), false, RedisMessageType.Double);
                        case '#': return new RedisResult<object>(ReadBoolean(c), false, RedisMessageType.Boolean);

                        case '*': return new RedisResult<object>(ReadArray(c), false, RedisMessageType.Array);
                        case '~': return new RedisResult<object>(ReadArray(c), false, RedisMessageType.Set);
                        case '>': return new RedisResult<object>(ReadArray(c), false, RedisMessageType.Push);
                        case '%': return new RedisResult<object>(ReadMap(c), false, RedisMessageType.Map);
                        case '|': return new RedisResult<object>(ReadMap(c), false, RedisMessageType.Attribute);
                        case '.': ReadLine(null); return new RedisResult<object>(null, true, RedisMessageType.SimpleString); //无类型
                        case ' ': continue;
                        default: throw new ProtocolViolationException($"Expecting fail MessageType '{c}'");
                    }
                }
            }

            void Read(Stream outStream, int len, int bufferSize = 1024)
            {
                if (len <= 0) return;
                var buffer = new byte[Math.Min(bufferSize, len)];
                var bufferLength = buffer.Length;
                while (true)
                {
                    var readed = _stream.Read(buffer, 0, bufferLength);
                    if (readed <= 0) throw new ProtocolViolationException($"Expecting fail Read surplus length: {len}");
                    if (readed > 0) outStream.Write(buffer, 0, readed);
                    len = len - readed;
                    if (len <= 0) break;
                    if (len < buffer.Length) bufferLength = len;
                }
            }
            string ReadLine(Stream outStream)
            {
                var sb = outStream == null ? new StringBuilder() : null;
                var buffer = new byte[1];
                var should_break = false;
                while (true)
                {
                    var readed = _stream.Read(buffer, 0, 1);
                    if (readed <= 0) throw new ProtocolViolationException($"Expecting fail ReadLine end of stream");
                    if (buffer[0] == 13)
                        should_break = true;
                    else if (buffer[0] == 10 && should_break)
                        break;
                    else
                    {
                        if (outStream == null) sb.Append((char)buffer[0]);
                        else outStream.WriteByte(buffer[0]);
                        should_break = false;
                    }
                }
                return sb?.ToString();
            }
        }

        class Resp3Writer
        {
            Stream _stream;
            Encoding _encoding;
            RedisProtocol _protocol;

            public Resp3Writer(Stream stream, Encoding encoding, RedisProtocol protocol)
            {
                _stream = stream;
                _encoding = encoding ?? Encoding.UTF8;
                _protocol = protocol;
            }

            public void WriteCommand(List<object> cmd)
            {
                WriteNumber('*', cmd.Count);
                foreach (var c in cmd)
                {
                    if (c is byte[]) WriteClob(c as byte[]);
                    else if (c is Enum) WriteBlobString(c.ToInvariantCultureToString().ToUpper());
                    else WriteBlobString(c.ToInvariantCultureToString());
                }
            }

            readonly byte[] Crlf = new byte[] { 13, 10 };
            readonly byte[] Null = new byte[] { 93, 13, 10 }; //_\r\n
            Resp3Writer WriteBlobString(string text, char msgtype = '$')
            {
                if (text == null) return WriteNull();
                return WriteClob(_encoding.GetBytes(text), msgtype);
            }
            Resp3Writer WriteClob(byte[] data, char msgtype = '$')
            {
                if (data == null) return WriteNull();
                var size = _encoding.GetBytes($"{msgtype}{data.Length}\r\n");
                _stream.Write(size, 0, size.Length);
                _stream.Write(data, 0, data.Length);
                _stream.Write(Crlf, 0, Crlf.Length);
                return this;
            }
            Resp3Writer WriteSimpleString(string text)
            {
                if (text == null) return WriteNull();
                if (text.Contains("\r\n")) return WriteBlobString(text);
                return WriteRaw($"+{text}\r\n");
            }
            Resp3Writer WriteVerbatimString(string text) => WriteBlobString(text, '=');
            Resp3Writer WriteBlobError(string error) => WriteBlobString(error, '!');
            Resp3Writer WriteSimpleError(string error)
            {
                if (error == null) return WriteNull();
                if (error.Contains("\r\n"))
                {
                    if (_protocol == RedisProtocol.RESP2) return WriteSimpleString(error.Replace("\r\n", " "));
                    return WriteBlobError(error);
                }
                return WriteRaw($"-{error}\r\n");
            }
            Resp3Writer WriteNumber(char mstype, object number)
            {
                if (number == null) return WriteNull();
                return WriteRaw($"{mstype}{number.ToInvariantCultureToString()}\r\n");
            }
            Resp3Writer WriteDouble(double? number)
            {
                if (number == null) return WriteNull();
                if (_protocol == RedisProtocol.RESP2) return WriteBlobString(number.ToInvariantCultureToString());
                switch (number)
                {
                    case double.PositiveInfinity: return WriteRaw($",inf\r\n");
                    case double.NegativeInfinity: return WriteRaw($",-inf\r\n");
                    default: return WriteRaw($",{number.ToInvariantCultureToString()}\r\n");
                }
            }
            Resp3Writer WriteBoolean(bool? val)
            {
                if (val == null) return WriteNull();
                if (_protocol == RedisProtocol.RESP2) return WriteNumber(':', val.Value ? 1 : 0);
                return WriteRaw(val.Value ? $"#t\r\n" : "#f\r\n");
            }
            Resp3Writer WriteNull()
            {
                if (_protocol == RedisProtocol.RESP2) return WriteBlobString("");
                _stream.Write(Null, 0, Null.Length);
                return this;
            }

            Resp3Writer WriteRaw(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return this;
                var data = _encoding.GetBytes($"{raw.ToInvariantCultureToString()}");
                _stream.Write(data, 0, data.Length);
                return this;
            }

            public Resp3Writer WriteObject(object obj)
            {
                if (obj == null) WriteNull();
                if (obj is string str) return WriteBlobString(str);
                if (obj is byte[] byt) return WriteClob(byt);
                var objtype = obj.GetType();
                if (_dicIsNumberType.Value.TryGetValue(objtype, out var tryval))
                {
                    switch (tryval)
                    {
                        case 1: return WriteNumber(':', obj);
                        case 2: return WriteDouble((double?)obj);
                        case 3: return WriteNumber(',', obj);
                    }
                }
                objtype = objtype.NullableTypeOrThis();
                if (objtype == typeof(bool)) return WriteBoolean((bool?)obj);
                if (objtype.IsEnum) return WriteNumber(':', obj);
                if (objtype == typeof(char)) return WriteBlobString(obj.ToString());
                if (objtype == typeof(DateTime)) return WriteBlobString(obj.ToString());
                if (objtype == typeof(DateTimeOffset)) return WriteBlobString(((DateTimeOffset)obj).ToString("yyyy-MM-dd HH:mm:ss.fff zzzz"));
                if (objtype == typeof(TimeSpan)) return WriteBlobString(obj.ToInvariantCultureToString());
                if (objtype == typeof(BigInteger)) return WriteNumber('(', obj);
                if (obj is Exception ex) return WriteSimpleError(ex.Message);

                if (obj is IDictionary dic)
                {
                    if (_protocol == RedisProtocol.RESP2) WriteNumber('*', dic.Count * 2);
                    else WriteNumber('%', dic.Count);
                    foreach (var key in dic.Keys)
                        WriteObject(key).WriteObject(dic[key]);
                    return this;
                }
                if (obj is IEnumerable ie)
                {
                    using (var ms = new MemoryStream())
                    {
                        var msWriter = new Resp3Writer(ms, _encoding, _protocol);
                        var idx = 0;
                        foreach (var z in ie)
                        {
                            msWriter.WriteObject(z);
                            idx++;
                        }
                        if (idx > 0 && ms.Length > 0)
                        {
                            WriteNumber('*', idx);
                            ms.Position = 0;
                            ms.CopyTo(_stream);
                        }
                        ms.Close();
                    }
                    return this;
                }

                var ps = objtype.GetPropertiesDictIgnoreCase().Values;
                if (_protocol == RedisProtocol.RESP2) WriteNumber('*', ps.Count * 2);
                else WriteNumber('%', ps.Count);
                foreach (var p in ps)
                {
                    var pvalue = p.GetValue(obj, null);
                    WriteObject(p.Name).WriteObject(pvalue);
                }
                return this;
            }
        }

        #region ExpressionTree
        static int SetPropertyValueSupportExpressionTreeFlag = 1;
        static ConcurrentDictionary<Type, ConcurrentDictionary<string, Action<object, string, object>>> _dicSetEntityValueWithPropertyName = new ConcurrentDictionary<Type, ConcurrentDictionary<string, Action<object, string, object>>>();
        public static void SetPropertyValue(this Type entityType, object entity, string propertyName, object value)
        {
            if (entity == null) return;
            if (entityType == null) entityType = entity.GetType();

            if (SetPropertyValueSupportExpressionTreeFlag == 0)
            {
                var props = GetPropertiesDictIgnoreCase(entityType);
                if (props.TryGetValue(propertyName, out var prop) == false)
                    throw new Exception($"{entityType.DisplayCsharp()} 未找到属性名 {propertyName}");
                prop.SetValue(entity, value);
                return;
            }

            Action<object, string, object> func = null;
            try
            {
                func = _dicSetEntityValueWithPropertyName
                    .GetOrAdd(entityType, et => new ConcurrentDictionary<string, Action<object, string, object>>())
                    .GetOrAdd(propertyName, pn =>
                    {
                        var t = entityType;
                        var props = GetPropertiesDictIgnoreCase(t);
                        var parm1 = Expression.Parameter(typeof(object));
                        var parm2 = Expression.Parameter(typeof(string));
                        var parm3 = Expression.Parameter(typeof(object));
                        var var1Parm = Expression.Variable(t);
                        var exps = new List<Expression>(new Expression[] {
                            Expression.Assign(var1Parm, Expression.TypeAs(parm1, t))
                        });
                        if (props.ContainsKey(pn))
                        {
                            var prop = props[pn];
                            exps.Add(
                                Expression.Assign(
                                    Expression.MakeMemberAccess(var1Parm, prop),
                                    Expression.Convert(
                                        parm3,
                                        prop.PropertyType
                                    )
                                )
                            );
                        }
                        return Expression.Lambda<Action<object, string, object>>(Expression.Block(new[] { var1Parm }, exps), new[] { parm1, parm2, parm3 }).Compile();
                    });
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref SetPropertyValueSupportExpressionTreeFlag, 0);
                SetPropertyValue(entityType, entity, propertyName, value);
                return;
            }
            func(entity, propertyName, value);
        }
        #endregion

        #region 常用缓存的反射方法
        static Lazy<Dictionary<Type, byte>> _dicIsNumberType = new Lazy<Dictionary<Type, byte>>(() => new Dictionary<Type, byte>
        {
            [typeof(sbyte)] = 1,
            [typeof(sbyte?)] = 1,
            [typeof(short)] = 1,
            [typeof(short?)] = 1,
            [typeof(int)] = 1,
            [typeof(int?)] = 1,
            [typeof(long)] = 1,
            [typeof(long?)] = 1,
            [typeof(byte)] = 1,
            [typeof(byte?)] = 1,
            [typeof(ushort)] = 1,
            [typeof(ushort?)] = 1,
            [typeof(uint)] = 1,
            [typeof(uint?)] = 1,
            [typeof(ulong)] = 1,
            [typeof(ulong?)] = 1,
            [typeof(double)] = 2,
            [typeof(double?)] = 2,
            [typeof(float)] = 2,
            [typeof(float?)] = 2,
            [typeof(decimal)] = 3,
            [typeof(decimal?)] = 3
        });
        static bool IsIntegerType(this Type that) => that == null ? false : (_dicIsNumberType.Value.TryGetValue(that, out var tryval) ? tryval == 1 : false);
        static bool IsNumberType(this Type that) => that == null ? false : _dicIsNumberType.Value.ContainsKey(that);
        static bool IsNullableType(this Type that) => that.IsArray == false && that?.FullName.StartsWith("System.Nullable`1[") == true;
        static bool IsAnonymousType(this Type that) => that?.FullName.StartsWith("<>f__AnonymousType") == true;
        static bool IsArrayOrList(this Type that) => that == null ? false : (that.IsArray || typeof(IList).IsAssignableFrom(that));
        static Type NullableTypeOrThis(this Type that) => that?.IsNullableType() == true ? that.GetGenericArguments().First() : that;
        static string DisplayCsharp(this Type type, bool isNameSpace = true)
        {
            if (type == null) return null;
            if (type == typeof(void)) return "void";
            if (type.IsGenericParameter) return type.Name;
            if (type.IsArray) return $"{DisplayCsharp(type.GetElementType())}[]";
            var sb = new StringBuilder();
            var nestedType = type;
            while (nestedType.IsNested)
            {
                sb.Insert(0, ".").Insert(0, DisplayCsharp(nestedType.DeclaringType, false));
                nestedType = nestedType.DeclaringType;
            }
            if (isNameSpace && string.IsNullOrWhiteSpace(nestedType.Namespace) == false)
                sb.Insert(0, ".").Insert(0, nestedType.Namespace);

            if (type.IsGenericType == false)
                return sb.Append(type.Name).ToString();

            var genericParameters = type.GetGenericArguments();
            if (type.IsNested && type.DeclaringType.IsGenericType)
            {
                var dic = genericParameters.ToDictionary(a => a.Name);
                foreach (var nestedGenericParameter in type.DeclaringType.GetGenericArguments())
                    if (dic.ContainsKey(nestedGenericParameter.Name))
                        dic.Remove(nestedGenericParameter.Name);
                genericParameters = dic.Values.ToArray();
            }
            if (genericParameters.Any() == false)
                return sb.Append(type.Name).ToString();

            sb.Append(type.Name.Remove(type.Name.IndexOf('`'))).Append("<");
            var genericTypeIndex = 0;
            foreach (var genericType in genericParameters)
            {
                if (genericTypeIndex++ > 0) sb.Append(", ");
                sb.Append(DisplayCsharp(genericType, true));
            }
            return sb.Append(">").ToString();
        }
        static string DisplayCsharp(this MethodInfo method, bool isOverride)
        {
            if (method == null) return null;
            var sb = new StringBuilder();
            if (method.IsPublic) sb.Append("public ");
            if (method.IsAssembly) sb.Append("internal ");
            if (method.IsFamily) sb.Append("protected ");
            if (method.IsPrivate) sb.Append("private ");
            if (method.IsPrivate) sb.Append("private ");
            if (method.IsStatic) sb.Append("static ");
            if (method.IsAbstract && method.DeclaringType.IsInterface == false) sb.Append("abstract ");
            if (method.IsVirtual && method.DeclaringType.IsInterface == false) sb.Append(isOverride ? "override " : "virtual ");
            sb.Append(method.ReturnType.DisplayCsharp()).Append(" ").Append(method.Name);

            var genericParameters = method.GetGenericArguments();
            if (method.DeclaringType.IsNested && method.DeclaringType.DeclaringType.IsGenericType)
            {
                var dic = genericParameters.ToDictionary(a => a.Name);
                foreach (var nestedGenericParameter in method.DeclaringType.DeclaringType.GetGenericArguments())
                    if (dic.ContainsKey(nestedGenericParameter.Name))
                        dic.Remove(nestedGenericParameter.Name);
                genericParameters = dic.Values.ToArray();
            }
            if (genericParameters.Any())
                sb.Append("<")
                    .Append(string.Join(", ", genericParameters.Select(a => a.DisplayCsharp())))
                    .Append(">");

            sb.Append("(").Append(string.Join(", ", method.GetParameters().Select(a => $"{a.ParameterType.DisplayCsharp()} {a.Name}"))).Append(")");
            return sb.ToString();
        }
        static object CreateInstanceGetDefaultValue(this Type that)
        {
            if (that == null) return null;
            if (that == typeof(string)) return default(string);
            if (that == typeof(Guid)) return default(Guid);
            if (that.IsArray) return Array.CreateInstance(that, 0);
            if (that.IsInterface || that.IsAbstract) return null;
            var ctorParms = that.InternalGetTypeConstructor0OrFirst(false)?.GetParameters();
            if (ctorParms == null || ctorParms.Any() == false) return Activator.CreateInstance(that, true);
            return Activator.CreateInstance(that, ctorParms
                .Select(a => a.ParameterType.IsInterface || a.ParameterType.IsAbstract || a.ParameterType == typeof(string) || a.ParameterType.IsArray ?
                null :
                Activator.CreateInstance(a.ParameterType, null)).ToArray());
        }
        static ConcurrentDictionary<Type, ConstructorInfo> _dicInternalGetTypeConstructor0OrFirst = new ConcurrentDictionary<Type, ConstructorInfo>();
        static ConstructorInfo InternalGetTypeConstructor0OrFirst(this Type that, bool isThrow = true)
        {
            var ret = _dicInternalGetTypeConstructor0OrFirst.GetOrAdd(that, tp =>
                tp.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[0], null) ??
                tp.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault());
            if (ret == null && isThrow) throw new ArgumentException($"{that.FullName} 类型无方法访问构造函数");
            return ret;
        }

        static ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> _dicGetPropertiesDictIgnoreCase = new ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>>();
        static Dictionary<string, PropertyInfo> GetPropertiesDictIgnoreCase(this Type that) => that == null ? null : _dicGetPropertiesDictIgnoreCase.GetOrAdd(that, tp =>
        {
            var props = that.GetProperties().GroupBy(p => p.DeclaringType).Reverse().SelectMany(p => p);
            var dict = new Dictionary<string, PropertyInfo>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var prop in props)
            {
                if (dict.ContainsKey(prop.Name)) continue;
                dict.Add(prop.Name, prop);
            }
            return dict;
        });
        #endregion

        #region 类型转换
        public static string ToInvariantCultureToString(this object obj) => string.Format(CultureInfo.InvariantCulture, @"{0}", obj);
        public static T MapToClass<T>(this object[] list, Encoding encoding)
        {
            if (list?.Length % 2 != 0) throw new ArgumentException(nameof(list));
            var ttype = typeof(T);
            var ret = (T)ttype.CreateInstanceGetDefaultValue();
            var props = ttype.GetPropertiesDictIgnoreCase();
            for (var a = 0; a < list.Length; a += 2)
            {
                var name = list[a].ToString().Replace("-", "");
                if (props.TryGetValue(name, out var tryprop) == false) throw new ArgumentException($"{typeof(T).DisplayCsharp()} undefined Property {list[a]}");
                var val = list[a + 1];
                if (val == null) continue;
                ttype.SetPropertyValue(ret, tryprop.Name, tryprop.PropertyType.FromObject(val, encoding));
            }
            return ret;
        }
        public static Dictionary<string, T> MapToHash<T>(this object[] list, Encoding encoding)
        {
            if (list?.Length % 2 != 0) throw new ArgumentException(nameof(list));
            var dic = new Dictionary<string, T>();
            for (var a = 0; a < list.Length; a += 2)
            {
                var key = list[a].ToInvariantCultureToString();
                if (dic.ContainsKey(key)) continue;
                var val = list[a + 1];
                if (val == null) dic.Add(key, default(T));
                dic.Add(key, val is T conval ? conval : (T)typeof(T).FromObject(list[a + 1], encoding));
            }
            return dic;
        }
        internal static T ConvertTo<T>(this object value) => (T)typeof(T).FromObject(value);
        static ConcurrentDictionary<Type, Func<string, object>> _dicFromObject = new ConcurrentDictionary<Type, Func<string, object>>();
        public static object FromObject(this Type targetType, object value, Encoding encoding = null)
        {
            if (encoding == null) encoding = Encoding.UTF8;
            var valueIsNull = value == null;
            var valueType = valueIsNull ? typeof(string) : value.GetType();
            if (valueType == targetType) return value;
            if (valueType == typeof(byte[])) //byte[] -> guid
            {
                if (targetType == typeof(Guid))
                {
                    var bytes = value as byte[];
                    return Guid.TryParse(BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 36)).Replace("-", ""), out var tryguid) ? tryguid : Guid.Empty;
                }
                if (targetType == typeof(Guid?))
                {
                    var bytes = value as byte[];
                    return Guid.TryParse(BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 36)).Replace("-", ""), out var tryguid) ? (Guid?)tryguid : null;
                }
            }
            if (targetType == typeof(byte[])) //guid -> byte[]
            {
                if (valueIsNull) return null;
                if (valueType == typeof(Guid) || valueType == typeof(Guid?))
                {
                    var bytes = new byte[16];
                    var guidN = ((Guid)value).ToString("N");
                    for (var a = 0; a < guidN.Length; a += 2)
                        bytes[a / 2] = byte.Parse($"{guidN[a]}{guidN[a + 1]}", NumberStyles.HexNumber);
                    return bytes;
                }
                return encoding.GetBytes(value.ToInvariantCultureToString());
            }
            else if (targetType.IsArray)
            {
                if (value is IList valueList)
                {
                    var sourceArrLen = valueList.Count;
                    var targetElementType = targetType.GetElementType();
                    var target = Array.CreateInstance(targetElementType, sourceArrLen);
                    for (var a = 0; a < sourceArrLen; a++) target.SetValue(targetElementType.FromObject(valueList[a], encoding), a);
                    return target;
                }
                if (value is Array valueArr)
                {
                    var sourceArrLen = valueArr.Length;
                    var targetElementType = targetType.GetElementType();
                    var target = Array.CreateInstance(targetElementType, sourceArrLen);
                    for (var a = 0; a < sourceArrLen; a++) target.SetValue(targetElementType.FromObject(valueArr.GetValue(a), encoding), a);
                    return target;
                }
            }
            var func = _dicFromObject.GetOrAdd(targetType, tt =>
            {
                if (tt == typeof(object)) return vs => vs;
                if (tt == typeof(string)) return vs => vs;
                if (tt == typeof(char[])) return vs => vs == null ? null : vs.ToCharArray();
                if (tt == typeof(char)) return vs => vs == null ? default(char) : vs.ToCharArray(0, 1).FirstOrDefault();
                if (tt == typeof(bool)) return vs =>
                {
                    if (vs == null) return false;
                    switch (vs.ToLower())
                    {
                        case "true":
                        case "1":
                            return true;
                    }
                    return false;
                };
                if (tt == typeof(bool?)) return vs =>
                {
                    if (vs == null) return false;
                    switch (vs.ToLower())
                    {
                        case "true":
                        case "1":
                            return true;
                        case "false":
                        case "0":
                            return false;
                    }
                    return null;
                };
                if (tt == typeof(byte)) return vs => vs == null ? 0 : (byte.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(byte?)) return vs => vs == null ? null : (byte.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (byte?)tryval : null);
                if (tt == typeof(decimal)) return vs => vs == null ? 0 : (decimal.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(decimal?)) return vs => vs == null ? null : (decimal.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (decimal?)tryval : null);
                if (tt == typeof(double)) return vs => vs == null ? 0 : (double.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(double?)) return vs => vs == null ? null : (double.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (double?)tryval : null);
                if (tt == typeof(float)) return vs => vs == null ? 0 : (float.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(float?)) return vs => vs == null ? null : (float.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (float?)tryval : null);
                if (tt == typeof(int)) return vs => vs == null ? 0 : (int.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(int?)) return vs => vs == null ? null : (int.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (int?)tryval : null);
                if (tt == typeof(long)) return vs => vs == null ? 0 : (long.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(long?)) return vs => vs == null ? null : (long.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (long?)tryval : null);
                if (tt == typeof(sbyte)) return vs => vs == null ? 0 : (sbyte.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(sbyte?)) return vs => vs == null ? null : (sbyte.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (sbyte?)tryval : null);
                if (tt == typeof(short)) return vs => vs == null ? 0 : (short.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(short?)) return vs => vs == null ? null : (short.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (short?)tryval : null);
                if (tt == typeof(uint)) return vs => vs == null ? 0 : (uint.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(uint?)) return vs => vs == null ? null : (uint.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (uint?)tryval : null);
                if (tt == typeof(ulong)) return vs => vs == null ? 0 : (ulong.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(ulong?)) return vs => vs == null ? null : (ulong.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (ulong?)tryval : null);
                if (tt == typeof(ushort)) return vs => vs == null ? 0 : (ushort.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(ushort?)) return vs => vs == null ? null : (ushort.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (ushort?)tryval : null);
                if (tt == typeof(DateTime)) return vs => vs == null ? DateTime.MinValue : (DateTime.TryParse(vs, out var tryval) ? tryval : DateTime.MinValue);
                if (tt == typeof(DateTime?)) return vs => vs == null ? null : (DateTime.TryParse(vs, out var tryval) ? (DateTime?)tryval : null);
                if (tt == typeof(DateTimeOffset)) return vs => vs == null ? DateTimeOffset.MinValue : (DateTimeOffset.TryParse(vs, out var tryval) ? tryval : DateTimeOffset.MinValue);
                if (tt == typeof(DateTimeOffset?)) return vs => vs == null ? null : (DateTimeOffset.TryParse(vs, out var tryval) ? (DateTimeOffset?)tryval : null);
                if (tt == typeof(TimeSpan)) return vs => vs == null ? TimeSpan.Zero : (TimeSpan.TryParse(vs, out var tryval) ? tryval : TimeSpan.Zero);
                if (tt == typeof(TimeSpan?)) return vs => vs == null ? null : (TimeSpan.TryParse(vs, out var tryval) ? (TimeSpan?)tryval : null);
                if (tt == typeof(Guid)) return vs => vs == null ? Guid.Empty : (Guid.TryParse(vs, out var tryval) ? tryval : Guid.Empty);
                if (tt == typeof(Guid?)) return vs => vs == null ? null : (Guid.TryParse(vs, out var tryval) ? (Guid?)tryval : null);
                if (tt == typeof(BigInteger)) return vs => vs == null ? 0 : (BigInteger.TryParse(vs, NumberStyles.Any, null, out var tryval) ? tryval : 0);
                if (tt == typeof(BigInteger?)) return vs => vs == null ? null : (BigInteger.TryParse(vs, NumberStyles.Any, null, out var tryval) ? (BigInteger?)tryval : null);
                var localTargetType = targetType;
                var localValueType = valueType;
                return vs =>
                {
                    if (vs == null) return null;
                    throw new NotSupportedException($"convert failed {localValueType.DisplayCsharp()} -> {localTargetType.DisplayCsharp()}");
                };
            });
            var valueStr = valueIsNull ? null : (valueType == typeof(byte[]) ? encoding.GetString(value as byte[]) : value.ToInvariantCultureToString());
            return func(valueStr);
        }
        #endregion
    }

    public class RedisException : Exception
    {
        public RedisException(string message) : base(message) { }
    }
    public class RedisResult<T>
    {
        public T Value { get; }
        internal bool IsEnd { get; }
        public RedisMessageType MessageType { get; }
        public bool IsError => this.MessageType == RedisMessageType.SimpleError || this.MessageType == RedisMessageType.BlobError;
        public string SimpleError { get; }
        internal RedisResult(T value, bool isend, RedisMessageType msgtype) : this(value, value?.ConvertTo<string>(), isend, msgtype) { }
        internal RedisResult(T value, string simpleError, bool isend, RedisMessageType msgtype)
        {
            this.Value = value;
            this.IsEnd = isend;
            this.MessageType = msgtype;
            this.SimpleError = simpleError;
        }
        public RedisResult<T2> NewValue<T2>(Func<T, T2> value)
        {
            if (typeof(T) == typeof(T2)) return this as RedisResult<T2>;
            return new RedisResult<T2>(value(this.Value), this.SimpleError, true, this.MessageType);
        }
        public T ThrowOrValue()
        {
            if (IsError) throw new RedisException(this.SimpleError);
            return this.Value;
        }
    }

    public enum RedisMessageType
    {
        /// <summary>
        /// $11\r\nhelloworld\r\n
        /// </summary>
        BlobString,

        /// <summary>
        /// +hello world\r\n
        /// </summary>
        SimpleString,

        /// <summary>
        /// =15\r\ntxt:Some string\r\n
        /// </summary>
        VerbatimString,

        /// <summary>
        /// -ERR this is the error description\r\n<para></para>
        /// The first word in the error is in upper case and describes the error code.
        /// </summary>
        SimpleError,

        /// <summary>
        /// !21\r\nSYNTAX invalid syntax\r\n<para></para>
        /// The first word in the error is in upper case and describes the error code.
        /// </summary>
        BlobError,

        /// <summary>
        /// :1234\r\n
        /// </summary>
        Number,

        /// <summary>
        /// (3492890328409238509324850943850943825024385\r\n
        /// </summary>
        BigNumber,

        /// <summary>
        /// _\r\n
        /// </summary>
        Null,

        /// <summary>
        /// ,1.23\r\n<para></para>
        /// ,inf\r\n<para></para>
        /// ,-inf\r\n
        /// </summary>
        Double,

        /// <summary>
        /// #t\r\n<para></para>
        /// #f\r\n
        /// </summary>
        Boolean,

        /// <summary>
        /// *3\r\n:1\r\n:2\r\n:3\r\n<para></para>
        /// [1, 2, 3]
        /// </summary>
        Array,

        /// <summary>
        /// ~5\r\n+orange\r\n+apple\r\n#t\r\n:100\r\n:999\r\n
        /// </summary>
        Set,

        /// <summary>
        /// >4\r\n+pubsub\r\n+message\r\n+somechannel\r\n+this is the message\r\n
        /// </summary>
        Push,

        /// <summary>
        /// %2\r\n+first\r\n:1\r\n+second\r\n:2\r\n<para></para>
        /// { "first": 1, "second": 2 }
        /// </summary>
        Map,

        /// <summary>
        /// |2\r\n+first\r\n:1\r\n+second\r\n:2\r\n<para></para>
        /// { "first": 1, "second": 2 }
        /// </summary>
        Attribute,
    }

    public enum RedisProtocol { RESP2, RESP3 }
}