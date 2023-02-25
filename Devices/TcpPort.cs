﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Quva.Devices
{
    public class TcpPort : IComPort, IAsyncDisposable
    {
        public PortType PortType { get; } = PortType.Tcp;
        public ComParameter ComParameter { get; set; }
        public TcpParameter TcpParameter { get; set; }
        //Runtime:
        public uint Bcc { get; set; }
        public bool IsConnected() => tcpClient != null;

        public TcpPort(string paramstring)
        {
            ComParameter = new();
            TcpParameter = new();
            SetParamString(paramstring);
            inBuff = new(4096);
            outBuff = new(4096);
        }

    protected virtual async ValueTask DisposeAsyncCore()
        {
            Log.Warning($"{nameof(TcpPort)}.DisposeAsyncCore({tcpClient != null})");
            if (tcpClient != null)
            {
                await Task.Run(() => { tcpClient.Dispose(); });
            }
            tcpClient = null;
            if (tcpServer != null)
            {
                await Task.Run(() => { tcpServer.Stop(); });
            }
            tcpServer = null;
        }

        public async ValueTask DisposeAsync()
        {
            Log.Warning($"{nameof(TcpPort)}.DisposeAsync()");
            await DisposeAsyncCore().ConfigureAwait(false);

            GC.SuppressFinalize(this);
        }


        // Setzt Parameter
        // zB "COM1:9600:8:1:N" oder "localhost:1234" oder "listen:1234"
        private void SetParamString(string paramstring)
        {
            var SL = paramstring.Split(":");
            if (SL.Length != 2)
                throw new ArgumentException("Wrong Paramstring. Must be Host:Port or Listen:Port", nameof(paramstring));
            TcpParameter.ParamString = paramstring;
            TcpParameter.Remote = Remote.Client;
            TcpParameter.Host = SL[0];
            TcpParameter.Port = int.Parse(SL[1]);
            if (SL[0].Equals("Listen", StringComparison.OrdinalIgnoreCase))
            {
                TcpParameter.Remote = Remote.Host;
                TcpParameter.Host = "localhost";
            }
        }

        private TcpClient? tcpClient;
        private TcpListener? tcpServer;
        private IPHostEntry? ipHostEntry;
        private IPAddress? ipAddress;
        private IPEndPoint? ipEndPoint;
        private NetworkStream? stream;
        private ByteBuff inBuff;
        private ByteBuff outBuff;

        public async Task OpenAsync()
        {
            if (IsConnected())
            {
                Log.Warning($"TCP({TcpParameter.ParamString}): allready opened");
                return;
            }
            Log.Information($"TcpPort.OpenAsync Host:{TcpParameter.Host} Port:{TcpParameter.Port}");
            ipHostEntry = await Dns.GetHostEntryAsync(TcpParameter.Host ?? "localhost");
            //ipAddress = ipHostEntry.AddressList[0];
            // only IPV4:
            ipAddress = ipHostEntry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) 
                ?? ipHostEntry.AddressList[0];
            ipEndPoint = new IPEndPoint(ipAddress, TcpParameter.Port);

            if (TcpParameter.Remote == Remote.Host) 
                throw new NotImplementedException($"TCP({TcpParameter.ParamString}): Remote.Host");

            tcpClient = new TcpClient();
            try
            {
                Log.Debug($"ConnectAsync EndPoint:{ipEndPoint} - {ipAddress.AddressFamily}");
                await tcpClient.ConnectAsync(ipEndPoint);
                stream = tcpClient.GetStream();
                if (ComParameter.TimeoutMs == 0)
                    ComParameter.TimeoutMs = 10000;  //overwritable in Desc. Bevore: Timeout.Infinite;
                stream.ReadTimeout = ComParameter.TimeoutMs;
                stream.WriteTimeout = ComParameter.TimeoutMs;

            }
            catch
            {
                tcpClient = null;
                throw;
            }
        }

        public async Task CloseAsync()
        {
            if (!IsConnected())
            {
                Log.Warning($"TCP({TcpParameter.ParamString}): allready closed");
                return;
            }
            Log.Debug($"TCP({TcpParameter.ParamString}): CloseAsync");

            if (TcpParameter.Remote == Remote.Host)
                throw new NotImplementedException($"TCP({TcpParameter.ParamString}): Remote.Host");

            try
            {
                await Task.Run(() => { tcpClient?.Close(); });
            }
            finally
            {
                tcpClient = null;
                stream = null;
            }
        }

        #region input/output

        public async Task ResetAsync()
        {
            if (IsConnected())
                await CloseAsync();
            await OpenAsync();
        }

        //muss vor Read aufgerufen werden. Ruft Flush auf.
        public async Task<int> InCountAsync()
        {
            await FlushAsync();
            ArgumentNullException.ThrowIfNull(tcpClient);
            if (tcpClient.Available > 0)
            {
                int n = await tcpClient.GetStream().ReadAsync(inBuff.Buff, inBuff.Cnt, inBuff.Buff.Length - inBuff.Cnt);
                inBuff.Cnt += n;
            }
            return await Task.FromResult(inBuff.Cnt);
        }

        // read with timeout. into Buffer offset 0; max buffer.Cnt bytes. Returns 0 when no data available
        public async Task<int> ReadAsync(ByteBuff buffer)
        {
            int result = 0;
            // read from network into internal buffer, with timeout, only if internal buffer not long enough:
            if (inBuff.Cnt < buffer.Cnt)
            {
                await FlushAsync();
                ArgumentNullException.ThrowIfNull(tcpClient, nameof(tcpClient));
                Task<int> readTask = tcpClient.GetStream().ReadAsync(inBuff.Buff, inBuff.Cnt, inBuff.Buff.Length - inBuff.Cnt);
                await Task.WhenAny(readTask, Task.Delay(ComParameter.TimeoutMs));  //<-- timeout
                if (!readTask.IsCompleted)
                {
                    try
                    {
                        tcpClient.Close();
                    }
                    catch (Exception ex)
                    {
                        // maybe ObjectDisposedException - https://stackoverflow.com/questions/62161695
                        Log.Warning($"error closing TcpPort at ReadAsync {TcpParameter.ParamString}", ex);
                    }
                }
                else
                {
                    inBuff.Cnt += await readTask;
                }
            }
            // move from internal buffer[0..] to buffer[0..]; shift internal buffer
            if (inBuff.Cnt > 0)
            {
                result = inBuff.MoveTo(buffer);  //max buffer.Cnt
            }
            return await Task.FromResult(result);
        }

        // write only to internal buffer. Write to network later in flush.
        public async Task<bool> WriteAsync(ByteBuff buffer)
        {
            bool result = false;

            buffer.AppendTo(outBuff);

            return await Task.FromResult(result);
        }

        //muss vor Read und vor InCount und am Telegram Ende aufgerufen werden
        public async Task FlushAsync()
        {
            if (outBuff.Cnt > 0)
            {
                ArgumentNullException.ThrowIfNull(tcpClient, nameof(tcpClient));
                Task writeTask = tcpClient.GetStream().WriteAsync(outBuff.Buff, 0, outBuff.Cnt);
                await Task.WhenAny(writeTask, Task.Delay(ComParameter.TimeoutMs));  //<-- timeout
                outBuff.Cnt = 0;
                if (!writeTask.IsCompleted)
                {
                    try
                    {
                        tcpClient.Close();
                    }
                    catch (Exception ex)
                    {
                        // maybe ObjectDisposedException - https://stackoverflow.com/questions/62161695
                        Log.Warning($"error closing TcpPort at WriteAsync {TcpParameter.ParamString}", ex);
                    }
                }

            }
        }

        #endregion input/output



    }

    public class TcpParameter
    {
        public Remote Remote { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; }

        public string? ParamString { get; set; }
    }
}
