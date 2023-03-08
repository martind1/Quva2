﻿using Devices.Data;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Quva.Devices;

public class ComDevice
{
    private readonly ILogger CLog;
    private Device? device;    // from database table 
    public Device Device { get => device ?? throw new ArgumentNullException(); set => device = value; }
    public string Code { get; set; }
    // work items:
    public IComPort? ComPort { get; set; }
    public IScaleApi? ScaleApi { get; set; }
    public ICardApi? CardApi { get; set; }
    public IDisplayApi? DisplayApi { get; set; }


    public ComDevice()
    {
        //Code = devicecode; * kein Parameter wg CS0304
        CLog = Log.ForContext<DeviceService>();
        Code = string.Empty;
        slim = new SemaphoreSlim(1);
    }

    public virtual async Task Open()
    {
        // ComPort öffnen
        if (ComPort == null)
        {
            throw new NullReferenceException("ComPort is null");
        }
        if (!ComPort.IsConnected())
        {
            CLog.Information($"[{Code}] ComDevice.Open:");
            await ComPort.OpenAsync();
            CLog.Information($"[{Code}] ComDevice.Open OK");
        }
    }

    public virtual async Task Close()
    {
        // Dispose ComPort 
        CLog.Information($"[{Code}] ComDevice.Close({ComPort != null},{ComPort?.IsConnected()}):");
        if (ComPort != null && ComPort.IsConnected())
        {
            await ComPort.CloseAsync();
            CLog.Information($"[{Code}] ComDevice.Close OK");
        }
        if (timerAsync != null)
        {
            timerAsync.Dispose();
            timerAsync = null;
        }
    }

    public async Task Load()
    {
        CLog.Information($"[{Code}] ComDevice.Load");
        // [Code] von DB laden - erstmal von Test Service laden:
        var dataService = new DataService();
        device = await dataService.GetDevice(Code);

        ArgumentNullException.ThrowIfNull(Device.ParamString);
        ComPort = device.PortType switch
        {
            PortType.Tcp => new TcpPort(Code, Device.ParamString),
            PortType.None => null,
            _ => throw new NotImplementedException("only TCP implemented")
        };
        if (device.DeviceType == DeviceType.Scale)
        {
            ScaleApi = Device.ModulCode switch
            {
                "IT6000" => new ScaleIT6000(Code, this),
                "FAWAWS" => new ScaleFawaWs(Code, this),
                _ => throw new NotImplementedException($"Modulcode {Device.ModulCode}")
            };
        }
        if (device.DeviceType == DeviceType.Card)
        {
            CardApi = Device.ModulCode switch
            {
                "READER" => new CardReader(Code, this),
                _ => throw new NotImplementedException($"Modulcode {Device.ModulCode}")
            };
        }
    }

    public string Option(string key, string dflt)
    {
        ArgumentNullException.ThrowIfNull(Device.Options);
        if (!Device.Options.TryGetValue(key, out var result))
        {
            result = dflt;
        }
        return result;
    }

    public int Option(string key, int dflt)
    {
        try
        {
            return int.Parse(Option(key, dflt.ToString()));
        }
        catch (Exception ex)
        {
            CLog.Warning($"[{Code}] Fehler bei int Device.Option({key})", ex);
            return dflt;
        }
    }

    public float Option(string key, float dflt)
    {
        try
        {
            return int.Parse(Option(key, dflt.ToString()));
        }
        catch (Exception ex)
        {
            CLog.Warning($"[{Code}] Fehler bei float Device.Option({key})", ex);
            return dflt;
        }
    }

    private readonly SemaphoreSlim slim;
    private TimerAsync? timerAsync;
    private string timerCommand = string.Empty;

    #region Scale Commands

    public delegate void OnScaleStatus(ScaleData scaleData);
    public OnScaleStatus? onScaleStatus { get; set; }

    public async Task<ScaleData> ScaleCommand(string command)
    {
        ScaleData result;
        CLog.Debug($"[{Code}] WAIT Device.ScaleCommand({command})");
        // das ComDevice darf nur ein Command gleichzeitig ausführen (sonst Protokoll/TCP Murks)
        await slim.WaitAsync();
        try
        {
            CLog.Information($"[{Code}] START Device.ScaleCommand({command})");
            ArgumentNullException.ThrowIfNull(ScaleApi);
            result = await ScaleApi.ScaleCommand(command);
            CLog.Debug($"[{Code}] END Device.ScaleCommand({command})");
        }
        finally
        {
            slim.Release();
        }
        return await Task.FromResult(result);
    }
    public void ScaleCommandStart(string command, OnScaleStatus onScaleStatus)
    {
        CLog.Debug($"[{Code}] CALLBACK Device.ScaleCommandStart({command})");
        ArgumentNullException.ThrowIfNull(ScaleApi);
        this.onScaleStatus = onScaleStatus;
        timerCommand = command;
        timerAsync = new TimerAsync(OnScaleCommand, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));
    }

    private async Task OnScaleCommand(CancellationToken arg)
    {
        ScaleData result;
        CLog.Debug($"[{Code}] OnScaleCommand({timerCommand})");
        try
        {
            await Open();
            result = await ScaleCommand(timerCommand);
        }
        catch (Exception ex)
        {
            CLog.Warning($"[{Code}] Fehler OnScaleCommand({ex.Message})");
            await Close().ConfigureAwait(false);
            result = new ScaleData(Code, timerCommand)
            {
                ErrorNr = 99,
                ErrorText = ex.Message,
            };
        }
        ArgumentNullException.ThrowIfNull(onScaleStatus);
        onScaleStatus(result);
    }

    #endregion Scale Commands

    #region Card Commands

    public delegate void OnCardRead(CardData cardData);
    public OnCardRead? onCardRead { get; set; }

    public async Task<CardData> CardCommand(string command)
    {
        CardData result;
        CLog.Debug($"[{Code}] WAIT Device.CardCommand({command})");
        // das ComDevice darf nur ein Command gleichzeitig ausführen (sonst Protokoll/TCP Murks)
        await slim.WaitAsync();
        try
        {
            CLog.Information($"[{Code}] START Device.CardCommand({command})");
            ArgumentNullException.ThrowIfNull(CardApi);
            result = await CardApi.CardCommand(command);
            CLog.Debug($"[{Code}] END Device.CardCommand({command})");
        }
        finally
        {
            slim.Release();
        }
        return await Task.FromResult(result);
    }

    public void CardCommandStart(string command, OnCardRead onCardRead)
    {
        CLog.Debug($"[{Code}] CALLBACK Device.CardCommandStart({command})");
        ArgumentNullException.ThrowIfNull(CardApi);
        this.onCardRead = onCardRead;
        timerCommand = command;
        timerAsync = new TimerAsync(OnCardCommand, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));
    }

    private async Task OnCardCommand(CancellationToken arg)
    {
        CardData result;
        CLog.Debug($"[{Code}] OnCardCommand({timerCommand})");
        try
        {
            await Open();
            result = await CardCommand(timerCommand);
        }
        catch (Exception ex)
        {
            CLog.Warning($"[{Code}] Fehler OnCardCommand({ex.Message})");
            await Close().ConfigureAwait(false);
            result = new CardData(Code, timerCommand)
            {
                ErrorNr = 99,
                ErrorText = ex.Message,
            };
        }
        ArgumentNullException.ThrowIfNull(onCardRead);
        onCardRead(result);
    }

    #endregion Card Commands

    #region Display Commands

    public delegate void OnDisplayShow(DisplayData displayData);
    public OnDisplayShow? onDisplayShow { get; set; }  //timer function

    public async Task<DisplayData> DisplayCommand(string command, string message)
    {
        DisplayData result;
        CLog.Debug($"[{Code}] WAIT Device.DisplayCommand({command})");
        // das ComDevice darf nur ein Command gleichzeitig ausführen (sonst Protokoll/TCP Murks)
        await slim.WaitAsync();
        try
        {
            CLog.Information($"[{Code}] START Device.DisplayCommand({command})");
            ArgumentNullException.ThrowIfNull(DisplayApi);
            result = await DisplayApi.DisplayCommand(command, message);
            CLog.Debug($"[{Code}] END Device.DisplayCommand({command})");
        }
        finally
        {
            slim.Release();
        }
        return await Task.FromResult(result);
    }

    public void DisplayCommandStart(string command, OnDisplayShow onDisplayShow)
    {
        CLog.Debug($"[{Code}] CALLBACK Device.DisplayCommandStart({command})");
        ArgumentNullException.ThrowIfNull(DisplayApi);
        this.onDisplayShow = onDisplayShow;
        timerCommand = command;
        timerAsync = new TimerAsync(OnDisplayCommand, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));
    }

    private async Task OnDisplayCommand(CancellationToken arg)
    {
        var displayData = new DisplayData(Code, command: DisplayCommands.Show.ToString());
        CLog.Debug($"[{Code}] OnDisplayCommand({timerCommand})");
        try
        {
            await Open();
            ArgumentNullException.ThrowIfNull(onDisplayShow);
            onDisplayShow(displayData);  //fills .Message
            _ = await DisplayCommand(timerCommand, displayData.Message);
        }
        catch (Exception ex)
        {
            CLog.Warning($"[{Code}] Fehler OnDisplayCommand({ex.Message})");
            await Close().ConfigureAwait(false);
            _ = new DisplayData(Code, timerCommand)
            {
                ErrorNr = 99,
                ErrorText = ex.Message,
            };
        }
    }

    #endregion Display Commands
}
