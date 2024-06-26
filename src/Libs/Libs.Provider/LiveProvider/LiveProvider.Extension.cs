﻿// Copyright (c) Bili Copilot. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Bili.Copilot.Models.App.Args;
using Bili.Copilot.Models.App.Constants;
using Bili.Copilot.Models.BiliBili;
using Bili.Copilot.Models.Constants.Bili;
using Bili.Copilot.Models.Data.Live;

namespace Bili.Copilot.Libs.Provider;

/// <summary>
/// 提供直播相关的操作.
/// </summary>
public partial class LiveProvider
{
    private static readonly Lazy<LiveProvider> _lazyInstance = new(() => new LiveProvider());

    /// <summary>
    /// 直播间套接字.
    /// </summary>
    private ClientWebSocket _liveWebSocket;
    private CancellationTokenSource _liveCancellationToken;
    private bool _isLiveSocketConnected;

    private int _feedPageNumber;
    private int _partitionPageNumber;

    /// <summary>
    /// 直播间收到新的消息时发生.
    /// </summary>
    public event EventHandler<LiveMessageEventArgs> MessageReceived;

    /// <summary>
    /// 实例.
    /// </summary>
    public static LiveProvider Instance => _lazyInstance.Value;

    /// <summary>
    /// 对数据进行编码.
    /// </summary>
    /// <param name="msg">文本内容.</param>
    /// <param name="action">2=心跳，7=进房.</param>
    /// <returns>编码后的数据.</returns>
    private static byte[] EncodeLiveData(string msg, int action)
    {
        var data = Encoding.UTF8.GetBytes(msg);

        // 头部长度固定16
        var length = data.Length + 16;
        var buffer = new byte[length];
        using var ms = new MemoryStream(buffer);

        // 数据包长度
        var b = BitConverter.GetBytes(buffer.Length).ToArray().Reverse().ToArray();
        ms.Write(b, 0, 4);

        // 数据包头部长度,固定16
        b = BitConverter.GetBytes(16).Reverse().ToArray();
        ms.Write(b, 2, 2);

        // 协议版本，0=JSON,1=Int32,2=Buffer
        b = BitConverter.GetBytes(0).Reverse().ToArray();
        ms.Write(b, 0, 2);

        // 操作类型
        b = BitConverter.GetBytes(action).Reverse().ToArray();
        ms.Write(b, 0, 4);

        // 数据包头部长度,固定1
        b = BitConverter.GetBytes(1).Reverse().ToArray();
        ms.Write(b, 0, 4);

        // 数据
        ms.Write(data, 0, data.Length);

        var bytes = ms.ToArray();
        ms.Flush();
        return bytes;
    }

    /// <summary>
    /// 解压直播数据.
    /// </summary>
    /// <param name="data">数据.</param>
    /// <returns>解压后的数据.</returns>
    private static byte[] DecompressData(byte[] data)
    {
        using var outBuffer = new MemoryStream();
        using var compressedZipStream = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress);
        var block = new byte[1024];
        while (true)
        {
            var bytesRead = compressedZipStream.Read(block, 0, block.Length);
            if (bytesRead <= 0)
            {
                break;
            }
            else
            {
                outBuffer.Write(block, 0, bytesRead);
            }
        }

        compressedZipStream.Close();
        return outBuffer.ToArray();
    }

    private static async Task<string> GetBuvidAsync()
    {
        var request = await HttpProvider.GetRequestMessageAsync(System.Net.Http.HttpMethod.Get, ApiConstants.Account.Buvid, needCookie: true);
        var response = await HttpProvider.Instance.SendAsync(request);
        var buvidRes = await HttpProvider.ParseAsync<ServerResponse<BuvidResponse>>(response);
        return buvidRes.Data.B3;
    }

    private static async Task<LiveDanmakuResponse> GetLiveDanmakuInfoAsync(string roomId)
    {
        var queryParameters = new Dictionary<string, string>
        {
            { "id", roomId },
        };

        var request = await HttpProvider.GetRequestMessageAsync(System.Net.Http.HttpMethod.Get, ApiConstants.Live.WebDanmakuInformation, queryParameters, needCookie: true);
        var response = await HttpProvider.Instance.SendAsync(request);
        var danmakuRes = await HttpProvider.ParseAsync<ServerResponse<LiveDanmakuResponse>>(response);
        return danmakuRes.Data;
    }

    private void InitializeLiveSocket()
    {
        ResetLiveConnection();
        _liveWebSocket = new ClientWebSocket();
        _liveWebSocket.Options.SetRequestHeader("User-Agent", ServiceConstants.DefaultUserAgentString);
    }

    private async Task ConnectLiveSocketAsync(string host)
    {
        if (_isLiveSocketConnected)
        {
            return;
        }

        InitializeLiveSocket();
        await _liveWebSocket.ConnectAsync(new Uri($"wss://{host}/sub"), _liveCancellationToken.Token);
        _isLiveSocketConnected = _liveWebSocket?.State == WebSocketState.Open;
    }

    private async Task SendLiveMessageAsync(object data, int action)
    {
        var messageText = data is string str ? str : JsonSerializer.Serialize(data);
        var messageData = EncodeLiveData(messageText, action);
        await _liveWebSocket.SendAsync(messageData, WebSocketMessageType.Binary, true, _liveCancellationToken.Token);
    }

    private void ParseLiveData(byte[] data)
    {
        // 协议版本。
        // 0为JSON，可以直接解析；
        // 1为房间人气值,Body为Int32；
        // 2为压缩过Buffer，需要解压再处理
        var protocolVersion = BitConverter.ToInt32(new byte[4] { data[7], data[6], 0, 0 }, 0);

        // 操作类型。
        // 3=心跳回应，内容为房间人气值；
        // 5=通知，弹幕、广播等全部信息；
        // 8=进房回应，空
        var operation = BitConverter.ToInt32(data.Skip(8).Take(4).Reverse().ToArray(), 0);

        // 内容
        var body = data.Skip(16).ToArray();
        if (operation == 8)
        {
            MessageReceived?.Invoke(this, new LiveMessageEventArgs(LiveMessageType.ConnectSuccess, "弹幕连接成功"));
        }
        else if (operation == 3)
        {
            var online = BitConverter.ToInt32(body.Reverse().ToArray(), 0);
            MessageReceived?.Invoke(this, new LiveMessageEventArgs(LiveMessageType.Online, online));
        }
        else if (operation == 5)
        {
            if (protocolVersion == 2)
            {
                body = DecompressData(body);
            }

            var text = Encoding.UTF8.GetString(body);

            // 可能有多条数据，做个分割
            var textLines = Regex.Split(text, "[\x00-\x1f]+").Where(x => x.Length > 2 && x[0] == '{').ToArray();
            foreach (var item in textLines)
            {
                ParseMessage(item);
            }
        }
    }

    private void ParseMessage(string jsonMessage)
    {
        try
        {
#if DEBUG
            Debug.WriteLine(jsonMessage);
#endif
            var obj = JsonSerializer.Deserialize<JsonElement>(jsonMessage);
            var cmd = obj.GetProperty("cmd").ToString();
            if (cmd.Contains("DANMU_MSG"))
            {
                var msg = new LiveDanmakuMessage();
                if (obj.TryGetProperty("info", out var info) && info.GetArrayLength() != 0)
                {
                    var array = info.EnumerateArray().ToArray();
                    msg.Text = array.ElementAt(1).ToString();
                    if (array[2].GetArrayLength() != 0)
                    {
                        msg.UserName = array[2][1].ToString() + ":";

                        if (Convert.ToInt32(array[2][3].ToString()) == 1)
                        {
                            msg.VipText = "老爷";
                            msg.IsVip = true;
                        }

                        if (Convert.ToInt32(array[2][4].ToString()) == 1)
                        {
                            msg.VipText = "年费老爷";
                            msg.IsVip = false;
                            msg.IsBigVip = true;
                        }

                        if (Convert.ToInt32(array[2][2].ToString()) == 1)
                        {
                            msg.VipText = "房管";
                            msg.IsAdmin = true;
                        }
                    }

                    if (array[3].GetArrayLength() != 0)
                    {
                        msg.MedalName = array[3][1].ToString();
                        msg.MedalLevel = array[3][0].ToString();
                        msg.MedalColor = array[3][4].ToString();
                        msg.HasMedal = true;
                    }

                    if (array[4].GetArrayLength() != 0)
                    {
                        msg.Level = "UL" + array[4][0].ToString();
                        msg.LevelColor = array[4][2].ToString();
                    }

                    if (array[5].GetArrayLength() != 0)
                    {
                        msg.UserTitle = array[5][0].ToString();
                        msg.HasTitle = true;
                    }

                    var danmakuInfo = new LiveDanmakuInformation(msg.Text, msg.ContentColor, msg.UserName, msg.Level, msg.LevelColor, msg.IsAdmin);
                    MessageReceived?.Invoke(this, new LiveMessageEventArgs(LiveMessageType.Danmaku, danmakuInfo));
                }
            }
        }
        catch (Exception)
        {
            // TODO: 记录错误.
        }
    }
}
