using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ConstrainedExecution;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tup;

namespace AllLive.Core.Helper
{
    public class TupHttpHelper
    {
        private readonly string baseUrl = "";
        private readonly string servantName = "";
        readonly HttpClient httpClient;
        public TupHttpHelper(string baseUrl, string servantName)
        {
            this.baseUrl = baseUrl;
            this.servantName = servantName;
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            httpClient.BaseAddress = new Uri(baseUrl);
        }

        public async Task<Resp> GetAsync<Req, Resp>(Req req, string function,Resp proxy, CancellationToken cancellationToken = default(CancellationToken))
        {
            Resp result = proxy;
            try
            {
                TarsUniPacket uniPacket = new TarsUniPacket();
                uniPacket.ServantName = servantName;
                uniPacket.FuncName = function;
                uniPacket.setTarsVersion(Const.PACKET_TYPE_TUP3);
                uniPacket.setTarsPacketType(Const.PACKET_TYPE_TARSNORMAL);
                uniPacket.Put("tReq", req);
                byte[] array = uniPacket.Encode();
                using (var reqContent = new ByteArrayContent(array))
                {
                    // 设置content-type
                    reqContent.Headers.Add("Content-Type", "application/x-wup");
                    using (var response = await httpClient.PostAsync("", reqContent, cancellationToken).ConfigureAwait(false))
                    {
                        var responseBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        CoreDebug.Log(() => $"[Tars] {function} status={(int)response.StatusCode} len={responseBytes?.Length ?? 0}");
                        TarsUniPacket respPack = new TarsUniPacket();
                        respPack.Decode(responseBytes);
                        result = respPack.Get<Resp>("tRsp", result);
                        return result;
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                CoreDebug.Log(() => $"[Tars] {function} 请求失败: {ex.GetType().FullName} {ex.Message}");
                return result;
            }
        }




    }
}
