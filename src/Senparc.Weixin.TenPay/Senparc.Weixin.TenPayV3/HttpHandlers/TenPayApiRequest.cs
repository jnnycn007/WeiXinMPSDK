#region Apache License Version 2.0
/*----------------------------------------------------------------

Copyright 2026 Jeffrey Su & Suzhou Senparc Network Technology Co.,Ltd.

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
except in compliance with the License. You may obtain a copy of the License at

http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed under the
License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND,
either express or implied. See the License for the specific language governing permissions
and limitations under the License.

Detail: https://github.com/JeffreySu/WeiXinMPSDK/blob/master/license.md

----------------------------------------------------------------*/
#endregion Apache License Version 2.0

/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc
  
    文件名：TenPayApiRequest.cs
    文件功能描述：微信支付V3接口请求
    
    
    创建标识：Senparc - 20210815

    修改标识：Senparc - 20210822
    修改描述：重构使用ISenparcWeixinSettingForTenpayV3初始化实例

    修改标识：Senparc - 20211225
    修改描述：v0.5.2 发布版本删除调试代码

    修改标识：mojinxun - 20250618
    修改描述：v2.1.0 兼容微信平台证书和微信支付公钥 / PR #3144

    修改标识：Senparc - 20260718
    修改描述：v2.4.1 复用 HttpClient 并按请求隔离超时与资源释放

    修改标识：Senparc - 20260718
    修改描述：v2.5.0 复用序列化设置并支持请求取消与响应头优先读取

----------------------------------------------------------------*/

using Org.BouncyCastle.Crypto.Parameters;
using Senparc.CO2NET.Extensions;
using Senparc.CO2NET.Helpers;
using Senparc.CO2NET.Trace;
using Senparc.Weixin.Entities;
using Senparc.Weixin.Helpers;
using Senparc.Weixin.TenPayV3.Apis.Entities;
using Senparc.Weixin.TenPayV3.Helpers;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Senparc.Weixin.TenPayV3
{
    /// <summary>
    /// 微信支付 API 请求
    /// </summary>
    public class TenPayApiRequest
    {
        private static readonly Newtonsoft.Json.JsonSerializerSettings RequestJsonSerializerSettings = new()
        {
            NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore
        };
        private static readonly ConditionalWeakTable<ISenparcWeixinSettingForTenpayV3, Lazy<HttpClient>> HttpClients = new();
        private static readonly ConditionalWeakTable<Action<HttpClient>, ConditionalWeakTable<ISenparcWeixinSettingForTenpayV3, Lazy<HttpClient>>> CustomHttpClients = new();

        private readonly ISenparcWeixinSettingForTenpayV3 _tenpayV3Setting;
        private readonly Action<HttpClient> _setHeaderAction;
        private readonly Lazy<HttpClient> _client;

        public TenPayApiRequest(ISenparcWeixinSettingForTenpayV3 senparcWeixinSettingForTenpayV3 = null, Action<HttpClient> setHeaderAction = null)
        {
            _tenpayV3Setting = senparcWeixinSettingForTenpayV3 ?? Senparc.Weixin.Config.SenparcWeixinSetting.TenpayV3Setting;
            _setHeaderAction = setHeaderAction;
            _client = GetOrCreateHttpClient(_tenpayV3Setting, _setHeaderAction);
        }

        private static Lazy<HttpClient> GetOrCreateHttpClient(ISenparcWeixinSettingForTenpayV3 setting, Action<HttpClient> setHeaderAction)
        {
            var clients = setHeaderAction == null
                ? HttpClients
                : CustomHttpClients.GetValue(setHeaderAction, _ => new ConditionalWeakTable<ISenparcWeixinSettingForTenpayV3, Lazy<HttpClient>>());

            return clients.GetValue(setting, key => new Lazy<HttpClient>(
                () => CreateHttpClient(key, setHeaderAction),
                LazyThreadSafetyMode.ExecutionAndPublication));
        }

        private static HttpClient CreateHttpClient(ISenparcWeixinSettingForTenpayV3 setting, Action<HttpClient> setHeaderAction)
        {
            var client = new HttpClient(new TenPayHttpHandler(setting))
            {
                // 共享 HttpClient 不修改全局 Timeout，由每次请求自己的 CancellationToken 控制超时。
                Timeout = Timeout.InfiniteTimeSpan
            };

            SetDefaultHeaders(client);
            setHeaderAction?.Invoke(client);
            return client;
        }

        private static void SetDefaultHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            var userAgentValues = UserAgentValues.Instance;
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Senparc.Weixin.TenPayV3-C#", userAgentValues.TenPayV3Version));
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"(Senparc.Weixin {userAgentValues.SenparcWeixinVersion})"));
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(".NET", userAgentValues.RuntimeVersion));
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"({userAgentValues.OSVersion})"));
        }

        /// <summary>
        /// 设置 HTTP 请求头
        /// </summary>
        /// <param name="client"></param>
        public void SetHeader(HttpClient client)
        {
            SetDefaultHeaders(client);
            _setHeaderAction?.Invoke(client);
        }

        /// <summary>
        /// 获取 HttpResponseMessage 对象
        /// </summary> 
        /// <param name="url"></param>
        /// <param name="data">如果为 GET 请求，此参数可为 null</param>
        /// <param name="timeOut"></param>
        /// <param name="requestMethod"></param>
        /// <param name="checkDataNotNull">非 GET 请求情况下，是否强制检查 data 参数不能为 null，默认为 true</param>
        /// <returns>响应对象由调用方负责释放。</returns>
        public Task<HttpResponseMessage> GetHttpResponseMessageAsync(string url, object data, int timeOut = Config.TIME_OUT, ApiRequestMethod requestMethod = ApiRequestMethod.POST, bool checkDataNotNull = true)
        {
            return GetHttpResponseMessageAsync(url, data, CancellationToken.None, HttpCompletionOption.ResponseContentRead, timeOut, requestMethod, checkDataNotNull);
        }

        /// <summary>
        /// 获取 HttpResponseMessage 对象，并支持调用方取消及流式响应。
        /// </summary>
        /// <returns>响应对象由调用方负责释放。</returns>
        public async Task<HttpResponseMessage> GetHttpResponseMessageAsync(
            string url,
            object data,
            CancellationToken cancellationToken,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
            int timeOut = Config.TIME_OUT,
            ApiRequestMethod requestMethod = ApiRequestMethod.POST,
            bool checkDataNotNull = true)
        {
            if (timeOut <= 0 && timeOut != Timeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(nameof(timeOut), "超时时间必须大于 0，或使用 Timeout.Infinite。 ");
            }

            using var request = new HttpRequestMessage(GetHttpMethod(requestMethod), url);

            switch (requestMethod)
            {
                case ApiRequestMethod.GET:
                case ApiRequestMethod.DELETE:
                    WeixinTrace.Log(url);
                    break;
                case ApiRequestMethod.POST:
                case ApiRequestMethod.PUT:
                case ApiRequestMethod.PATCH:
                    if (checkDataNotNull)
                    {
                        _ = data ?? throw new ArgumentNullException($"{nameof(data)} 不能为 null！");
                    }

                    string jsonString = data != null
                        ? data.ToJson(false, RequestJsonSerializerSettings)
                        : "";
                    WeixinTrace.SendApiPostDataLog(url, jsonString);
                    request.Content = new StringContent(jsonString, Encoding.UTF8, mediaType: "application/json");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(requestMethod));
            }

            using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeOut != Timeout.Infinite)
            {
                timeoutCancellationTokenSource.CancelAfter(timeOut);
            }

            return await _client.Value.SendAsync(request, completionOption, timeoutCancellationTokenSource.Token).ConfigureAwait(false);
        }

        private static HttpMethod GetHttpMethod(ApiRequestMethod requestMethod)
        {
            switch (requestMethod)
            {
                case ApiRequestMethod.GET:
                    return HttpMethod.Get;
                case ApiRequestMethod.POST:
                    return HttpMethod.Post;
                case ApiRequestMethod.PUT:
                    return HttpMethod.Put;
                case ApiRequestMethod.PATCH:
                    return HttpMethod.Patch;
                case ApiRequestMethod.DELETE:
                    return HttpMethod.Delete;
                default:
                    throw new ArgumentOutOfRangeException(nameof(requestMethod));
            }
        }

        /// <summary>
        /// 请求参数，获取结果
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="url"></param>
        /// <param name="data">如果为 GET 请求，此参数可为 null</param>
        /// <param name="timeOut"></param>
        /// <param name="requestMethod"></param>
        /// <param name="checkSign"></param>
        /// <param name="createDefaultInstance"></param>
        /// <returns></returns>
        public Task<T> RequestAsync<T>(string url, object data, int timeOut = Config.TIME_OUT, ApiRequestMethod requestMethod = ApiRequestMethod.POST, bool checkSign = true, Func<T> createDefaultInstance = null)
            where T : ReturnJsonBase, new()
        {
            return RequestAsync(url, data, CancellationToken.None, timeOut, requestMethod, checkSign, createDefaultInstance);
        }

        /// <summary>
        /// 请求参数，获取结果，并支持调用方取消。
        /// </summary>
        public async Task<T> RequestAsync<T>(string url, object data, CancellationToken cancellationToken, int timeOut = Config.TIME_OUT, ApiRequestMethod requestMethod = ApiRequestMethod.POST, bool checkSign = true, Func<T> createDefaultInstance = null)
            where T : ReturnJsonBase, new()
        {
            T result = null;

            try
            {
                using HttpResponseMessage responseMessage = await GetHttpResponseMessageAsync(
                    url, data, cancellationToken, HttpCompletionOption.ResponseContentRead, timeOut, requestMethod).ConfigureAwait(false);

                //获取响应结果
                string content = await responseMessage.Content.ReadAsStringAsync();//TODO:如果不正确也要返回详情

#if DEBUG
                Console.WriteLine("Content:" + content + ",,Headers:" + responseMessage.Headers.ToString());
#endif

                //检查响应代码
                TenPayApiResultCode resultCode = TenPayApiResultCode.TryGetCode(responseMessage.StatusCode, content);

                if (resultCode.Success)
                {
                    if (resultCode.StateCode == ((int)HttpStatusCode.NoContent).ToString())
                    {
                        result = new T();
                        result.VerifySignSuccess = true;
                    }
                    else
                    {
                        //TODO:待测试
                        //验证微信签名
                        //result.Signed = VerifyTenpaySign(responseMessage.Headers, content);
                        var wechatpayTimestamp = responseMessage.Headers.GetValues("Wechatpay-Timestamp").First();
                        var wechatpayNonce = responseMessage.Headers.GetValues("Wechatpay-Nonce").First();
                        var wechatpaySignatureBase64 = responseMessage.Headers.GetValues("Wechatpay-Signature").First();//后续需要base64解码
                        var wechatpaySerial = responseMessage.Headers.GetValues("Wechatpay-Serial").First();

                        result = content.GetObject<T>();

                        if (checkSign)
                        {
                            try
                            {
                                var pubKey = await TenPayV3InfoCollection.GetAPIv3PublicKeyAsync(this._tenpayV3Setting, wechatpaySerial, cancellationToken).ConfigureAwait(false);
                                if (this._tenpayV3Setting.EncryptionType == CertType.SM)
                                {
                                    byte[] pubKeyBytes = Convert.FromBase64String(pubKey);
                                    ECPublicKeyParameters eCPublicKeyParameters = SMPemHelper.LoadPublicKeyToParameters(pubKeyBytes);

                                    //验签名串
                                    string contentForSign = $"{wechatpayTimestamp}\n{wechatpayNonce}\n{content}\n";

                                    result.VerifySignSuccess = GmHelper.VerifySm3WithSm2(eCPublicKeyParameters, contentForSign, wechatpaySignatureBase64);
                                }
                                else
                                {
                                    var isTenpayPubKey = TenPaySignHelper.IsPublicKey(wechatpaySerial);
                                    result.VerifySignSuccess = TenPaySignHelper.VerifyTenpaySign(_tenpayV3Setting.EncryptionType.Value, wechatpayTimestamp, wechatpayNonce, wechatpaySignatureBase64, content, pubKey, isTenpayPubKey);
                                }
                            }
                            catch (Exception ex)
                            {
                                throw new TenpayApiRequestException("RequestAsync 签名验证失败：" + ex.Message, ex);
                            }
                        }
                    }
                }
                else
                {
                    result = createDefaultInstance?.Invoke() ?? GetInstance<T>(true);
                    resultCode.Additional = content;
                }
                //T result = resultCode.Success ? (await responseMessage.Content.ReadAsStringAsync()).GetObject<T>() : new T();
                result.ResultCode = resultCode;

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SenparcTrace.BaseExceptionLog(ex);
                result = createDefaultInstance?.Invoke() ?? GetInstance<T>(false);
                if (result != null)
                {
                    result.ResultCode = new() { ErrorMessage = ex.Message };
                }

                return result;
            }
        }

        /// <summary>
        /// 获取实例
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="throwIfFaild"></param>
        /// <returns></returns>
        private T GetInstance<T>(bool throwIfFaild)
            where T : ReturnJsonBase
        {
            if (typeof(T).IsClass)
            {
                return Senparc.CO2NET.Helpers.ReflectionHelper.CreateInstance<T>(typeof(T).FullName, typeof(T).Assembly.GetName().Name);
            }
            else if (throwIfFaild)
            {
                throw new TenpayApiRequestException("GetInstance 失败，此类型无法自动生成：" + typeof(T).FullName);
            }
            return null;
        }
    }
}
