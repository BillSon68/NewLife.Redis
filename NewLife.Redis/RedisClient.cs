﻿using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NewLife.Collections;
using NewLife.Data;
using NewLife.Log;
using NewLife.Net;
using NewLife.Reflection;
using NewLife.Serialization;

//#nullable enable
namespace NewLife.Caching;

/// <summary>Redis客户端</summary>
/// <remarks>
/// 以极简原则进行设计，每个客户端不支持并行命令处理（非线程安全），可通过多客户端多线程解决。
/// </remarks>
public class RedisClient : DisposeBase
{
    #region 属性
    /// <summary>名称</summary>
    public String Name { get; set; }

    /// <summary>客户端</summary>
    public TcpClient Client { get; set; }

    /// <summary>服务器地址</summary>
    public NetUri Server { get; set; }

    /// <summary>宿主</summary>
    public Redis Host { get; set; }

    /// <summary>读写超时时间。默认为0取Host.Timeout</summary>
    public Int32 Timeout { get; set; }

    /// <summary>是否已登录</summary>
    public Boolean Logined { get; private set; }

    /// <summary>登录时间</summary>
    public DateTime LoginTime { get; private set; }
    #endregion

    #region 构造
    /// <summary>实例化</summary>
    /// <param name="redis">宿主</param>
    /// <param name="server">服务器地址。一个redis对象可能有多服务器，例如Cluster集群</param>
    public RedisClient(Redis redis, NetUri server)
    {
        Name = redis?.Name;
        Host = redis;
        Server = server;
    }

    /// <summary>销毁</summary>
    /// <param name="disposing"></param>
    protected override void Dispose(Boolean disposing)
    {
        base.Dispose(disposing);

        // 销毁时退出
        if (Logined)
        {
            try
            {
                var tc = Client;
                if (tc != null && tc.Connected && tc.GetStream() != null) Quit();
            }
            catch { }
        }

        Client.TryDispose();
    }

    /// <summary>已重载。</summary>
    /// <returns></returns>
    public override String ToString() => Server + "";
    #endregion

    #region 核心方法
    private Stream _stream;
    /// <summary>新建连接获取数据流</summary>
    /// <param name="create">新建连接</param>
    /// <returns></returns>
    private Stream GetStream(Boolean create)
    {
        var tc = Client;
        //NetworkStream ns = null;
        var ns = _stream;

        // 判断连接是否可用
        var active = false;
        try
        {
            //ns = tc?.GetStream();
            active = ns != null && tc != null && tc.Connected && ns.CanWrite && ns.CanRead;
        }
        catch { }

        // 如果连接不可用，则重新建立连接
        if (!active)
        {
            Logined = false;

            Client = null;
            tc.TryDispose();
            if (!create) return null;

            var timeout = Timeout;
            if (timeout == 0) timeout = Host.Timeout;
            tc = new TcpClient
            {
                SendTimeout = timeout,
                ReceiveTimeout = timeout
            };
            //tc.Connect(Server.Address, Server.Port);

            try
            {
                // 采用异步来解决连接超时设置问题
                var uri = Server;
                var ar = tc.BeginConnect(uri.Address, uri.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeout, true))
                {
                    tc.Close();
                    throw new TimeoutException($"连接[{uri}][{timeout}ms]超时！");
                }
                tc.EndConnect(ar);

                Client = tc;
                ns = tc.GetStream();

                // 客户端SSL
                var sp = Host.SslProtocol;
                if (sp != SslProtocols.None)
                {
                    var sslStream = new SslStream(ns, false, OnCertificateValidationCallback);
                    sslStream.AuthenticateAsClient(uri.Host ?? uri.Address + "", new X509CertificateCollection(), sp, false);

                    ns = sslStream;
                }

                _stream = ns;
            }
            catch
            {
                // 连接异常时，放弃该客户端连接对象。上层连接池将切换新的服务端节点
                Dispose();
                throw;
            }
        }

        return ns;
    }

    private Boolean OnCertificateValidationCallback(Object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        //WriteLog("Valid {0} {1}", certificate.Issuer, sslPolicyErrors);
        //if (chain?.ChainStatus != null)
        //{
        //    foreach (var item in chain.ChainStatus)
        //    {
        //        WriteLog("Chain {0} {1}", item.Status, item.StatusInformation?.Trim());
        //    }
        //}

        // 如果没有证书，全部通过
        var cert = Host.Certificate;
        if (cert == null) return true;

        return chain.ChainElements
                .Cast<X509ChainElement>()
                .Any(x => x.Certificate.Thumbprint == cert.Thumbprint);
    }

    private async Task<Stream> GetStreamAsync(Boolean create)
    {
        var tc = Client;
        //NetworkStream ns = null;
        var ns = _stream;

        // 判断连接是否可用
        var active = false;
        try
        {
            //ns = tc?.GetStream();
            active = ns != null && tc != null && tc.Connected && ns.CanWrite && ns.CanRead;
        }
        catch { }

        // 如果连接不可用，则重新建立连接
        if (!active)
        {
            Logined = false;

            Client = null;
            tc.TryDispose();
            if (!create) return null;

            var timeout = Timeout;
            if (timeout == 0) timeout = Host.Timeout;
            tc = new TcpClient
            {
                SendTimeout = timeout,
                ReceiveTimeout = timeout
            };

            var uri = Server;
            await tc.ConnectAsync(uri.Address, uri.Port);

            Client = tc;
            ns = tc.GetStream();

            // 客户端SSL
            var sp = Host.SslProtocol;
            if (sp != SslProtocols.None)
            {
                var sslStream = new SslStream(ns, false, OnCertificateValidationCallback);
                sslStream.AuthenticateAsClient(uri.Host ?? uri.Address + "", new X509CertificateCollection(), sp, false);

                ns = sslStream;
            }

            _stream = ns;
        }

        return ns;
    }

    private static readonly Byte[] _NewLine = new[] { (Byte)'\r', (Byte)'\n' };

    /// <summary>发出请求</summary>
    /// <param name="ms"></param>
    /// <param name="cmd"></param>
    /// <param name="args"></param>
    /// <param name="oriArgs">原始参数，仅用于输出日志</param>
    /// <returns></returns>
    protected virtual void GetRequest(Stream ms, String cmd, Packet[] args, Object[] oriArgs)
    {
        // *<number of arguments>\r\n$<number of bytes of argument 1>\r\n<argument data>\r\n
        // *1\r\n$4\r\nINFO\r\n

        var log = Log == null || Log == Logger.Null ? null : Pool.StringBuilder.Get();
        log?.Append(cmd);

        /*
         * 一颗玲珑心
         * 九天下凡尘
         * 翩翩起菲舞
         * 霜摧砺石开
         */

        // 区分有参数和无参数
        if (args == null || args.Length == 0)
        {
            //var str = "*1\r\n${0}\r\n{1}\r\n".F(cmd.Length, cmd);
            ms.Write(GetHeaderBytes(cmd, 0));
        }
        else
        {
            //var str = "*{2}\r\n${0}\r\n{1}\r\n".F(cmd.Length, cmd, 1 + args.Length);
            ms.Write(GetHeaderBytes(cmd, args.Length));

            for (var i = 0; i < args.Length; i++)
            {
                var item = args[i];
                var size = item.Total;
                var sizes = size.ToString().GetBytes();

                // 指令日志。简单类型显示原始值，复杂类型显示序列化后字符串
                if (log != null)
                {
                    log.Append(' ');
                    var ori = oriArgs?[i];
                    switch (ori.GetType().GetTypeCode())
                    {
                        case TypeCode.Object:
                            log.AppendFormat("[{0}]{1}", size, item.ToStr(null, 0, 1024)?.TrimEnd());
                            break;
                        case TypeCode.DateTime:
                            log.Append(((DateTime)ori).ToString("yyyy-MM-dd HH:mm:ss.fff"));
                            break;
                        default:
                            log.Append(ori);
                            break;
                    }
                }

                //str = "${0}\r\n".F(item.Length);
                //ms.Write(str.GetBytes());
                ms.WriteByte((Byte)'$');
                ms.Write(sizes);
                ms.Write(_NewLine);
                //ms.Write(item);
                item.CopyTo(ms);
                ms.Write(_NewLine);
            }
        }
        if (log != null) WriteLog("=> {0}", log.Put(true));
    }

    /// <summary>接收响应</summary>
    /// <param name="ns"></param>
    /// <param name="count"></param>
    /// <returns></returns>
    protected virtual IList<Object> GetResponse(Stream ns, Int32 count)
    {
        /*
         * 响应格式
         * 1：简单字符串，非二进制安全字符串，一般是状态回复。  +开头，例：+OK\r\n 
         * 2: 错误信息。-开头， 例：-ERR unknown command 'mush'\r\n
         * 3: 整型数字。:开头， 例：:1\r\n
         * 4：大块回复值，最大512M。  $开头+数据长度。 例：$4\r\nmush\r\n
         * 5：多条回复。*开头， 例：*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n
         */

        var ms = new BufferedStream(ns);
        var log = Log == null || Log == Logger.Null ? null : Pool.StringBuilder.Get();

        // 多行响应
        var list = new List<Object>();
        for (var i = 0; i < count; i++)
        {
            // 解析响应
            var b = ms.ReadByte();
            if (b == -1) break;

            var header = (Char)b;
            log?.Append(header);
            if (header == '$')
            {
                list.Add(ReadBlock(ms, log));
            }
            else if (header == '*')
            {
                list.Add(ReadBlocks(ms, log));
            }
            else
            {
                // 字符串以换行为结束符
                var str = ReadLine(ms);
                log?.Append(str);

                if (header is '+' or ':')
                    list.Add(str);
                else if (header == '-')
                    throw new Exception(str);
                else
                {
                    XTrace.WriteLine("无法解析响应[{0:X2}] {1}", (Byte)header, ms.ReadBytes(-1).ToHex("-"));
                    throw new InvalidDataException($"无法解析响应 [{header}]");
                }
            }
        }

        if (log != null) WriteLog("<= {0}", log.Put(true));

        return list;
    }

    /// <summary>执行命令，发请求，取响应</summary>
    /// <param name="cmd"></param>
    /// <param name="args"></param>
    /// <param name="oriArgs">原始参数，仅用于输出日志</param>
    /// <returns></returns>
    protected virtual Object ExecuteCommand(String cmd, Packet[] args, Object[] oriArgs)
    {
        var isQuit = cmd == "QUIT";

        var ns = GetStream(!isQuit);
        if (ns == null) return null;

        if (!cmd.IsNullOrEmpty())
        {
            // 验证登录
            CheckLogin(cmd);
            CheckSelect(cmd);

            var ms = Pool.MemoryStream.Get();
            GetRequest(ms, cmd, args, oriArgs);

            var max = Host.MaxMessageSize;
            if (max > 0 && ms.Length > max) throw new InvalidOperationException($"命令[{cmd}]的数据包大小[{ms.Length}]超过最大限制[{max}]，大key会拖累整个Redis实例，可通过Redis.MaxMessageSize调节。");

            // WriteTo与位置无关，CopyTo与位置相关
            //ms.Position = 0;
            if (ms.Length > 0) ms.WriteTo(ns);
            ms.Put();
        }

        var rs = GetResponse(ns, 1);

        if (isQuit) Logined = false;

        return rs.FirstOrDefault();
    }

    /// <summary>异步接收响应</summary>
    /// <param name="ns">网络数据流</param>
    /// <param name="count">响应个数</param>
    /// <param name="cancellationToken">取消通知</param>
    /// <returns></returns>
    protected virtual async Task<IList<Object>> GetResponseAsync(Stream ns, Int32 count, CancellationToken cancellationToken)
    {
        /*
         * 响应格式
         * 1：简单字符串，非二进制安全字符串，一般是状态回复。  +开头，例：+OK\r\n 
         * 2: 错误信息。-开头， 例：-ERR unknown command 'mush'\r\n
         * 3: 整型数字。:开头， 例：:1\r\n
         * 4：大块回复值，最大512M。  $开头+数据长度。 例：$4\r\nmush\r\n
         * 5：多条回复。*开头， 例：*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n
         */

        var list = new List<Object>();
        var ms = ns;
        var log = Log == null || Log == Logger.Null ? null : Pool.StringBuilder.Get();

        // 取巧进行异步操作，只要异步读取到第一个字节，后续同步读取
        var buf = new Byte[1];
        if (cancellationToken == CancellationToken.None)
            cancellationToken = new CancellationTokenSource(Timeout > 0 ? Timeout : Host.Timeout).Token;
        var n = await ms.ReadAsync(buf, 0, buf.Length, cancellationToken);
        if (n <= 0) return list;

        var header = (Char)buf[0];

        // 多行响应
        for (var i = 0; i < count; i++)
        {
            // 解析响应
            if (i > 0)
            {
                var b = ms.ReadByte();
                if (b == -1) break;

                header = (Char)b;
            }

            log?.Append(header);
            if (header == '$')
            {
                list.Add(ReadBlock(ms, log));
            }
            else if (header == '*')
            {
                list.Add(ReadBlocks(ms, log));
            }
            else
            {
                // 字符串以换行为结束符
                var str = ReadLine(ms);
                log?.Append(str);

                if (header is '+' or ':')
                    list.Add(str);
                else if (header == '-')
                    throw new Exception(str);
                else
                {
                    XTrace.WriteLine("无法解析响应[{0:X2}] {1}", (Byte)header, ms.ReadBytes(-1).ToHex("-"));
                    throw new InvalidDataException($"无法解析响应 [{header}]");
                }
            }
        }

        if (log != null) WriteLog("<= {0}", log.Put(true));

        return list;
    }

    /// <summary>异步执行命令，发请求，取响应</summary>
    /// <param name="cmd">命令</param>
    /// <param name="args">参数数组</param>
    /// <param name="oriArgs">原始参数，仅用于输出日志</param>
    /// <param name="cancellationToken">取消通知</param>
    /// <returns></returns>
    protected virtual async Task<Object> ExecuteCommandAsync(String cmd, Packet[] args, Object[] oriArgs, CancellationToken cancellationToken)
    {
        var isQuit = cmd == "QUIT";

        var ns = await GetStreamAsync(!isQuit);
        if (ns == null) return null;

        if (!cmd.IsNullOrEmpty())
        {
            // 验证登录
            CheckLogin(cmd);
            CheckSelect(cmd);

            var ms = Pool.MemoryStream.Get();
            GetRequest(ms, cmd, args, oriArgs);

            // WriteTo与位置无关，CopyTo与位置相关
            ms.Position = 0;
            if (ms.Length > 0) await ms.CopyToAsync(ns, 4096, cancellationToken);
            ms.Put();

            await ns.FlushAsync(cancellationToken);
        }

        var rs = await GetResponseAsync(ns, 1, cancellationToken);

        if (isQuit) Logined = false;

        return rs.FirstOrDefault();
    }

    private void CheckLogin(String cmd)
    {
        if (Logined) return;
        if (cmd.EqualIgnoreCase("Auth")) return;

        if (!Host.Password.IsNullOrEmpty() && !Auth(Host.UserName, Host.Password))
            throw new Exception("登录失败！");

        Logined = true;
        LoginTime = DateTime.Now;
    }

    private Int32 _selected = -1;
    private void CheckSelect(String cmd)
    {
        var db = Host.Db;
        if (_selected == db) return;
        if (cmd.EqualIgnoreCase("Auth", "Select", "Info")) return;

        if (db > 0 && (Host is not FullRedis rds || !rds.Mode.EqualIgnoreCase("cluster", "sentinel"))) Select(db);

        _selected = db;
    }

    /// <summary>重置。干掉历史残留数据</summary>
    public void Reset()
    {
        var ns = GetStream(false);
        if (ns == null) return;

        // 干掉历史残留数据
        if (ns is NetworkStream nss && nss.DataAvailable)
        {
            var buf = new Byte[1024];

            Int32 count;
            do
            {
                count = ns.Read(buf, 0, buf.Length);
            } while (count > 0 && nss.DataAvailable);
        }
    }

    private static Packet ReadBlock(Stream ms, StringBuilder log) => ReadPacket(ms, log);

    private Object[] ReadBlocks(Stream ms, StringBuilder log)
    {
        // 结果集数量
        var len = ReadLine(ms).ToInt(-1);
        log?.Append(len);
        if (len < 0) return new Object[0];

        var arr = new Object[len];
        for (var i = 0; i < len; i++)
        {
            var b = ms.ReadByte();
            if (b == -1) break;

            var header = (Char)b;
            log?.Append(' ');
            log?.Append(header);
            if (header == '$')
            {
                arr[i] = ReadPacket(ms, log);
            }
            else if (header is '+' or ':')
            {
                arr[i] = ReadLine(ms);
                log?.Append(arr[i]);
            }
            else if (header == '*')
            {
                arr[i] = ReadBlocks(ms, log);
            }
        }

        return arr;
    }

    private static Packet ReadPacket(Stream ms, StringBuilder log)
    {
        var len = ReadLine(ms).ToInt(-1);
        log?.Append(len);
        if (len == 0)
        {
            // 某些字段即使长度是0，还是要把换行符读走
            ReadLine(ms);
            return null;
        }
        if (len <= 0) return null;
        //if (len <= 0) throw new InvalidDataException();

        var buf = new Byte[len + 2];
        var p = 0;
        while (p < buf.Length)
        {
            // 等待，直到读完需要的数据，避免大包丢数据
            var count = ms.Read(buf, p, buf.Length - p);
            if (count <= 0) break;

            p += count;
        }

        var pk = new Packet(buf, 0, p - 2);
        log?.AppendFormat(" {0}", pk.ToStr(null, 0, 1024)?.TrimEnd());

        return pk;
    }

    private static String ReadLine(Stream ms)
    {
        var sb = Pool.StringBuilder.Get();
        while (true)
        {
            var b = ms.ReadByte();
            if (b < 0) break;

            if (b == '\r')
            {
                var b2 = ms.ReadByte();
                if (b2 < 0) break;

                if (b2 == '\n') break;

                sb.Append((Char)b);
                sb.Append((Char)b2);
            }
            else
                sb.Append((Char)b);
        }

        return sb.Put(true);
    }
    #endregion

    #region 主要方法
    /// <summary>执行命令。返回字符串、Packet、Packet[]</summary>
    /// <param name="cmd"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    public virtual Object Execute(String cmd, params Object[] args)
    {
        // 埋点名称，支持二级命令
        var act = cmd.EqualIgnoreCase("cluster", "xinfo", "xgroup", "xreadgroup") ? $"{cmd}-{args?.FirstOrDefault()}" : cmd;
        using var span = cmd.IsNullOrEmpty() ? null : Host.Tracer?.NewSpan($"redis:{Name}:{act}", args);
        try
        {
            return ExecuteCommand(cmd, args?.Select(e => Host.Encoder.Encode(e)).ToArray(), args);
        }
        catch (Exception ex)
        {
            span?.SetError(ex, null);
            throw;
        }
    }

    /// <summary>执行命令。返回基本类型、对象、对象数组</summary>
    /// <param name="cmd"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    public virtual TResult Execute<TResult>(String cmd, params Object[] args)
    {
        // 管道模式
        if (_ps != null)
        {
            _ps.Add(new Command(cmd, args, typeof(TResult)));
            return default;
        }

        var rs = Execute(cmd, args);
        if (rs == null) return default;
        if (rs is TResult rs2) return rs2;
        if (rs != null && TryChangeType(rs, typeof(TResult), out var target)) return (TResult)target;

        return default;
    }

    /// <summary>尝试执行命令。返回基本类型、对象、对象数组</summary>
    /// <param name="cmd"></param>
    /// <param name="args"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    public virtual Boolean TryExecute<TResult>(String cmd, Object[] args, out TResult value)
    {
        var rs = Execute(cmd, args);
        if (rs is TResult rs2)
        {
            value = rs2;
            return true;
        }

        value = default;
        if (rs == null) return false;

        if (rs != null && TryChangeType(rs, typeof(TResult), out var target)) value = (TResult)target;

        return true;
    }

    /// <summary>读取更多。用于PubSub等多次读取命令</summary>
    /// <returns></returns>
    public virtual TResult ReadMore<TResult>()
    {
        var ns = GetStream(false);
        if (ns == null) return default;

        var rss = GetResponse(ns, 1);
        var rs = rss.FirstOrDefault();

        //var rs = ExecuteCommand(null, null, null);
        if (rs == null) return default;
        if (rs is TResult rs2) return rs2;
        if (rs != null && TryChangeType(rs, typeof(TResult), out var target)) return (TResult)target;

        return default;
    }

    /// <summary>异步执行命令。返回字符串、Packet、Packet[]</summary>
    /// <param name="cmd">命令</param>
    /// <param name="args">参数数组</param>
    /// <param name="cancellationToken">取消通知</param>
    /// <returns></returns>
    public virtual async Task<Object> ExecuteAsync(String cmd, Object[] args, CancellationToken cancellationToken = default)
    {
        // 埋点名称，支持二级命令
        var act = cmd.EqualIgnoreCase("cluster", "xinfo", "xgroup") ? $"{cmd}-{args?.FirstOrDefault()}" : cmd;
        using var span = cmd.IsNullOrEmpty() ? null : Host.Tracer?.NewSpan($"redis:{Name}:{act}", args);
        try
        {
            return await ExecuteCommandAsync(cmd, args?.Select(e => Host.Encoder.Encode(e)).ToArray(), args, cancellationToken);
        }
        catch (Exception ex)
        {
            span?.SetError(ex, null);
            throw;
        }
    }

    /// <summary>异步执行命令。返回基本类型、对象、对象数组</summary>
    /// <param name="cmd">命令</param>
    /// <param name="args">参数数组</param>
    /// <returns></returns>
    public virtual async Task<TResult> ExecuteAsync<TResult>(String cmd, params Object[] args) => await ExecuteAsync<TResult>(cmd, args, CancellationToken.None);

    /// <summary>异步执行命令。返回基本类型、对象、对象数组</summary>
    /// <param name="cmd">命令</param>
    /// <param name="args">参数数组</param>
    /// <param name="cancellationToken">取消通知</param>
    /// <returns></returns>
    public virtual async Task<TResult> ExecuteAsync<TResult>(String cmd, Object[] args, CancellationToken cancellationToken)
    {
        // 管道模式
        if (_ps != null)
        {
            _ps.Add(new Command(cmd, args, typeof(TResult)));
            return default;
        }

        var rs = await ExecuteAsync(cmd, args, cancellationToken);
        if (rs == null) return default;
        if (rs is TResult rs2) return rs2;
        if (rs != null && TryChangeType(rs, typeof(TResult), out var target)) return (TResult)target;

        return default;
    }

    /// <summary>读取更多。用于PubSub等多次读取命令</summary>
    /// <param name="cancellationToken">取消通知</param>
    /// <returns></returns>
    public virtual async Task<TResult> ReadMoreAsync<TResult>(CancellationToken cancellationToken)
    {
        var ns = await GetStreamAsync(false);
        if (ns == null) return default;

        var rss = await GetResponseAsync(ns, 1, cancellationToken);
        var rs = rss.FirstOrDefault();

        //var rs = ExecuteCommand(null, null, null);
        if (rs == null) return default;
        if (rs is TResult rs2) return rs2;
        if (rs != null && TryChangeType(rs, typeof(TResult), out var target)) return (TResult)target;

        return default;
    }

    /// <summary>尝试转换类型</summary>
    /// <param name="value"></param>
    /// <param name="type"></param>
    /// <param name="target"></param>
    /// <returns></returns>
    public virtual Boolean TryChangeType(Object value, Type type, out Object target)
    {
        target = null;

        if (value is String str)
        {
            try
            {
                //target = value.ChangeType(type);
                if (type == typeof(Boolean) && str == "OK")
                    target = true;
                else
                    target = Convert.ChangeType(str, type);
                return true;
            }
            catch (Exception ex)
            {
                //if (type.GetTypeCode() != TypeCode.Object)
                throw new Exception($"不能把字符串[{str}]转为类型[{type.FullName}]", ex);
            }
        }

        if (value is Packet pk)
        {
            target = Host.Encoder.Decode(pk, type);
            return true;
        }

        if (value is Object[] objs)
        {
            if (type == typeof(Object[])) { target = value; return true; }
            if (type == typeof(Packet[])) { target = objs.Cast<Packet>().ToArray(); return true; }

            // 遇到空结果时返回默认值
            if (objs.Length == 0) return false;

            var elmType = type.GetElementTypeEx();
            var arr = Array.CreateInstance(elmType, objs.Length);
            for (var i = 0; i < objs.Length; i++)
            {
                if (objs[i] is Packet pk3)
                    arr.SetValue(Host.Encoder.Decode(pk3, elmType), i);
                else if (objs[i] != null && objs[i].GetType().As(elmType))
                    arr.SetValue(objs[i], i);
            }
            target = arr;
            return true;
        }

        return false;
    }

    private IList<Command> _ps;
    /// <summary>管道命令个数</summary>
    public Int32 PipelineCommands => _ps == null ? 0 : _ps.Count;

    /// <summary>开始管道模式</summary>
    public virtual void StartPipeline() => _ps ??= new List<Command>();

    /// <summary>结束管道模式</summary>
    /// <param name="requireResult">要求结果</param>
    public virtual Object[] StopPipeline(Boolean requireResult)
    {
        var ps = _ps;
        if (ps == null) return null;

        _ps = null;

        var ns = GetStream(true);
        if (ns == null) return null;

        using var span = Host.Tracer?.NewSpan($"redis:{Name}:Pipeline", null);
        try
        {
            // 验证登录
            CheckLogin(null);
            CheckSelect(null);

            // 整体打包所有命令
            var ms = Pool.MemoryStream.Get();
            var cmds = new List<String>(ps.Count);
            foreach (var item in ps)
            {
                cmds.Add(item.Name);
                GetRequest(ms, item.Name, item.Args.Select(e => Host.Encoder.Encode(e)).ToArray(), item.Args);
            }

            // 设置数据标签
            span?.SetTag(cmds);

            // 整体发出
            if (ms.Length > 0) ms.WriteTo(ns);
            ms.Put();

            if (!requireResult) return new Object[ps.Count];

            // 获取响应
            var list = GetResponse(ns, ps.Count);
            for (var i = 0; i < list.Count; i++)
            {
                if (TryChangeType(list[i], ps[i].Type, out var target) && target != null) list[i] = target;
            }

            return list.ToArray();
        }
        catch (Exception ex)
        {
            span?.SetError(ex, null);
            throw;
        }
    }

    private class Command
    {
        public String Name { get; set; }
        public Object[] Args { get; set; }
        public Type Type { get; set; }

        public Command(String name, Object[] args, Type type)
        {
            Name = name;
            Args = args;
            Type = type;
        }
    }
    #endregion

    #region 基础功能
    /// <summary>心跳</summary>
    /// <returns></returns>
    public Boolean Ping() => Execute<String>("PING") == "PONG";

    /// <summary>选择Db</summary>
    /// <param name="db"></param>
    /// <returns></returns>
    public Boolean Select(Int32 db) => Execute<String>("SELECT", db + "") == "OK";

    /// <summary>验证密码</summary>
    /// <param name="username"></param>
    /// <param name="password"></param>
    /// <returns></returns>
    public Boolean Auth(String username, String password)
    {
        var rs = username.IsNullOrEmpty() ?
            Execute<String>("AUTH", password) :
            Execute<String>("AUTH", username, password);

        return rs == "OK";
    }

    /// <summary>退出</summary>
    /// <returns></returns>
    public Boolean Quit() => Execute<String>("QUIT") == "OK";
    #endregion

    #region 获取设置
    /// <summary>批量设置</summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="values"></param>
    /// <returns></returns>
    public Boolean SetAll<T>(IDictionary<String, T> values)
    {
        if (values == null || values.Count == 0) throw new ArgumentNullException(nameof(values));

        var ps = new List<Object>();
        foreach (var item in values)
        {
            ps.Add(item.Key);

            if (item.Value == null) throw new NullReferenceException();
            ps.Add(item.Value);
        }

        var rs = Execute<String>("MSET", ps.ToArray());
        if (rs != "OK")
        {
            using var span = Host.Tracer?.NewSpan($"redis:{Name}:ErrorSetAll", values);
            if (Host.ThrowOnFailure) throw new XException("Redis.SetAll({0})失败。{1}", values.ToJson(), rs);
        }

        return rs == "OK";
    }

    /// <summary>批量获取</summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="keys"></param>
    /// <returns></returns>
    public IDictionary<String, T> GetAll<T>(IEnumerable<String> keys)
    {
        if (keys == null || !keys.Any()) throw new ArgumentNullException(nameof(keys));

        var ks = keys.ToArray();

        var dic = new Dictionary<String, T>();
        if (Execute("MGET", ks) is not Object[] rs) return dic;

        for (var i = 0; i < ks.Length && i < rs.Length; i++)
        {
            if (rs[i] is Packet pk) dic[ks[i]] = (T)Host.Encoder.Decode(pk, typeof(T));
        }

        return dic;
    }
    #endregion

    #region 辅助
    private static readonly ConcurrentDictionary<String, Byte[]> _cache0 = new();
    private static readonly ConcurrentDictionary<String, Byte[]> _cache1 = new();
    private static readonly ConcurrentDictionary<String, Byte[]> _cache2 = new();
    private static readonly ConcurrentDictionary<String, Byte[]> _cache3 = new();
    /// <summary>获取命令对应的字节数组，全局缓存</summary>
    /// <param name="cmd"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    private static Byte[] GetHeaderBytes(String cmd, Int32 args = 0)
    {
        if (args == 0) return _cache0.GetOrAdd(cmd, k => $"*1\r\n${k.Length}\r\n{k}\r\n".GetBytes());
        if (args == 1) return _cache1.GetOrAdd(cmd, k => $"*2\r\n${k.Length}\r\n{k}\r\n".GetBytes());
        if (args == 2) return _cache2.GetOrAdd(cmd, k => $"*3\r\n${k.Length}\r\n{k}\r\n".GetBytes());
        if (args == 3) return _cache3.GetOrAdd(cmd, k => $"*4\r\n${k.Length}\r\n{k}\r\n".GetBytes());

        return $"*{1 + args}\r\n${cmd.Length}\r\n{cmd}\r\n".GetBytes();
    }
    #endregion

    #region 日志
    /// <summary>日志</summary>
    public ILog Log { get; set; } = Logger.Null;

    /// <summary>写日志</summary>
    /// <param name="format"></param>
    /// <param name="args"></param>
    public void WriteLog(String format, params Object[] args) => Log?.Info(format, args);
    #endregion
}
//#nullable restore