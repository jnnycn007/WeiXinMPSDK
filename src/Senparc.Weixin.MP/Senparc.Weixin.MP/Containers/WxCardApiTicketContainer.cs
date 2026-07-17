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

    文件名：WxCardApiTicketContainer.cs
    文件功能描述：WxCardApiTicketContainer


    创建标识：Senparc - 20180419

    修改标识：Senparc - 20180614
    修改描述：CO2NET v0.1.0 ContainerBag 取消属性变动通知机制，使用手动更新缓存
   
    修改标识：Senparc - 20180707
    修改描述：v15.0.9 Container 的 Register() 的微信参数自动添加到 Config.SenparcWeixinSetting.Items 下

    修改标识：Senparc - 20181226
    修改描述：v16.6.2 修改 DateTime 为 DateTimeOffset

    修改标识：Senparc - 20190421
    修改描述：v17.0.0 支持异步 Container
    
    修改标识：Senparc - 20190822
    修改描述：v16.7.13 完善同步方法的 WxCardApiTicketContainer.Register() 对异步方法的调用，避免可能的线程锁死问题

    修改标识：Senparc - 20190826
    修改描述：v16.7.15 优化 Register() 方法

    修改标识：Senparc - 20190827
    修改描述：v16.7.16 解决卡券WxCardApiTicketContainer【异步方法】获取可用Ticket,type传值的问题

----------------------------------------------------------------*/

using System;
using System.Linq;
using System.Threading.Tasks;
using Senparc.Weixin.Containers;
using Senparc.Weixin.Exceptions;
using Senparc.Weixin.MP.Entities;
using Senparc.Weixin.MP.CommonAPIs;
using Senparc.Weixin.Utilities.WeixinUtility;
using Senparc.CO2NET.CacheUtility;
using Senparc.CO2NET.Extensions;

namespace Senparc.Weixin.MP.Containers
{
    /// <summary>
    /// WxCardApiTicket包
    /// </summary>
    [Serializable]
    public class WxCardApiTicketBag : BaseContainerBag, IBaseContainerBag_AppId
    {
        public string AppId { get; set; }
        public string AppSecret { get; set; }
        public JsApiTicketResult WxCardApiTicketResult { get; set; }
        public DateTimeOffset WxCardApiTicketExpireTime { get; set; }
        /// <summary>
        /// 只针对这个AppId的锁
        /// </summary>
        internal object Lock = new object();

        //private DateTimeOffset _WxCardApiTicketExpireTime;
        //private JsApiTicketResult _WxCardApiTicketResult;
        //private string _appSecret;
        //private string _appId;
    }

    /// <summary>
    /// 通用接口WxCardApiTicket容器，用于自动管理WxCardApiTicket，如果过期会重新获取
    /// </summary>
    public class WxCardApiTicketContainer : BaseContainer<WxCardApiTicketBag>
    {
        const string LockResourceName = "MP.WxCardApiTicketContainer";

        #region 同步方法

        /// <summary>
        /// 注册应用凭证信息，此操作只是注册，不会马上获取Ticket，并将清空之前的Ticket，
        /// </summary>
        /// <param name="appId"></param>
        /// <param name="appSecret"></param>
        /// <param name="name">标记JsApiTicket名称（如微信公众号名称），帮助管理员识别。当 name 不为 null 和 空值时，本次注册内容将会被记录到 Senparc.Weixin.Config.SenparcWeixinSetting.Items[name] 中，方便取用。</param>
        /*此接口不提供异步方法*/
        [Obsolete("请使用 RegisterAsync() 方法")]
        public static void Register(string appId, string appSecret, string name = null)
        {
            //同步入口必须在返回前完成注册，否则紧接着读取容器时会出现未注册竞态。
            RegisterAsync(appId, appSecret, name).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        #region WxCardApiTicket

        /// <summary>
        /// 使用完整的应用凭证获取Ticket，如果不存在将自动注册
        /// </summary>
        /// <param name="appId"></param>
        /// <param name="appSecret"></param>
        /// <param name="getNewTicket"></param>
        /// <returns></returns>
        public static string TryGetWxCardApiTicket(string appId, string appSecret, bool getNewTicket = false)
        {
            if (!CheckRegistered(appId) || getNewTicket)
            {
                Register(appId, appSecret);
            }
            return GetWxCardApiTicket(appId);
        }

        /// <summary>
        /// 获取可用Ticket
        /// </summary>
        /// <param name="appId"></param>
        /// <param name="getNewTicket">是否强制重新获取新的Ticket</param>
        /// <returns></returns>
        public static string GetWxCardApiTicket(string appId, bool getNewTicket = false)
        {
            return GetWxCardApiTicketResult(appId, getNewTicket).ticket;
        }

        /// <summary>
        /// 获取可用Ticket
        /// </summary>
        /// <param name="appId"></param>
        /// <param name="getNewTicket">是否强制重新获取新的Ticket</param>
        /// <returns></returns>
        public static JsApiTicketResult GetWxCardApiTicketResult(string appId, bool getNewTicket = false)
        {
            if (!CheckRegistered(appId))
            {
                throw new UnRegisterAppIdException(null, "此appId尚未注册，请先使用WxCardApiTicketContainer.Register完成注册（全局执行一次即可）！");
            }

            WxCardApiTicketBag wxCardApiTicketBag = TryGetItem(appId);
            using (Cache.BeginCacheLock(LockResourceName, appId))//同步锁
            {
                wxCardApiTicketBag = TryGetItem(appId);//获锁后重新读取并二次检查过期状态
                if (getNewTicket || wxCardApiTicketBag.WxCardApiTicketExpireTime <= SystemTime.Now)
                {
                    //已过期，重新获取
                    wxCardApiTicketBag.WxCardApiTicketResult = CommonApi.GetTicket(wxCardApiTicketBag.AppId,
                                                                                   wxCardApiTicketBag.AppSecret,
                                                                                   "wx_card");
                    wxCardApiTicketBag.WxCardApiTicketExpireTime = ApiUtility.GetExpireTime(wxCardApiTicketBag.WxCardApiTicketResult.expires_in);
                    Update(wxCardApiTicketBag, null);
                }
            }
            return wxCardApiTicketBag.WxCardApiTicketResult;
        }

        #endregion

        #endregion


        #region 异步方法

        /// <summary>
        /// 【异步方法】注册应用凭证信息，此操作只是注册，不会马上获取Ticket，并将清空之前的Ticket，
        /// </summary>
        /// <param name="appId"></param>
        /// <param name="appSecret"></param>
        /// <param name="name">标记JsApiTicket名称（如微信公众号名称），帮助管理员识别。当 name 不为 null 和 空值时，本次注册内容将会被记录到 Senparc.Weixin.Config.SenparcWeixinSetting.Items[name] 中，方便取用。</param>
        /*此接口不提供异步方法*/
        public static async Task RegisterAsync(string appId, string appSecret, string name = null)
        {
            //记录注册信息，RegisterFunc委托内的过程会在缓存丢失之后自动重试
            RegisterFuncCollection[appId] = async () =>
            {
                //using(FlushCache.CreateInstance())
                //{
                WxCardApiTicketBag bag = new WxCardApiTicketBag()
                {
                    Name = name,
                    AppId = appId,
                    AppSecret = appSecret,
                    WxCardApiTicketExpireTime = DateTimeOffset.MinValue,
                    WxCardApiTicketResult = new JsApiTicketResult()
                };
                await UpdateAsync(appId, bag, null).ConfigureAwait(false);
                return bag;
                //}
            };

            await RegisterFuncCollection[appId]().ConfigureAwait(false);

            if (!name.IsNullOrEmpty())
            {
                Senparc.Weixin.Config.SenparcWeixinSetting.Items[name].WeixinAppId = appId;
                Senparc.Weixin.Config.SenparcWeixinSetting.Items[name].WeixinAppSecret = appSecret;
            }
        }

        #region WxCardApiTicket

        /// <summary>
        /// 【异步方法】使用完整的应用凭证获取Ticket，如果不存在将自动注册
        /// </summary>
        /// <param name="appId"></param>
        /// <param name="appSecret"></param>
        /// <param name="getNewTicket"></param>
        /// <returns></returns>
        public static async Task<string> TryGetWxCardApiTicketAsync(string appId,
                                                                    string appSecret,
                                                                    bool getNewTicket = false)
        {
            if (!await CheckRegisteredAsync(appId).ConfigureAwait(false) || getNewTicket)
            {
                await RegisterAsync(appId, appSecret).ConfigureAwait(false);
            }
            return await GetWxCardApiTicketAsync(appId, getNewTicket).ConfigureAwait(false);
        }

        /// <summary>
        ///【异步方法】 获取可用Ticket
        /// </summary>
        /// <param name="appId"></param>
        /// <param name="getNewTicket">是否强制重新获取新的Ticket</param>
        /// <returns></returns>
        public static async Task<string> GetWxCardApiTicketAsync(string appId, bool getNewTicket = false)
        {
            JsApiTicketResult result = await GetWxCardApiTicketResultAsync(appId, getNewTicket).ConfigureAwait(false);
            return result.ticket;
        }

        /// <summary>
        /// 【异步方法】获取可用Ticket
        /// </summary>
        /// <param name="appId"></param>
        /// <param name="getNewTicket">是否强制重新获取新的Ticket</param>
        /// <returns></returns>
        public static async Task<JsApiTicketResult> GetWxCardApiTicketResultAsync(string appId,
                                                                                  bool getNewTicket = false)
        {
            if (!await CheckRegisteredAsync(appId).ConfigureAwait(false))
            {
                throw new UnRegisterAppIdException(null, "此appId尚未注册，请先使用WxCardApiTicketContainer.Register完成注册（全局执行一次即可）！");
            }

            WxCardApiTicketBag wxCardApiTicketBag = await TryGetItemAsync(appId).ConfigureAwait(false);
            using (await Cache.BeginCacheLockAsync(LockResourceName, appId).ConfigureAwait(false))//同步锁
            {
                wxCardApiTicketBag = await TryGetItemAsync(appId).ConfigureAwait(false);//获锁后重新读取并二次检查过期状态
                if (getNewTicket || wxCardApiTicketBag.WxCardApiTicketExpireTime <= SystemTime.Now)
                {
                    //已过期，重新获取
                    JsApiTicketResult wxCardApiTicketResult = await CommonApi.GetTicketAsync(wxCardApiTicketBag.AppId,
                                                                                             wxCardApiTicketBag.AppSecret,"wx_card").ConfigureAwait(false);

                    wxCardApiTicketBag.WxCardApiTicketResult = wxCardApiTicketResult;
                    wxCardApiTicketBag.WxCardApiTicketExpireTime = SystemTime.Now.AddSeconds(wxCardApiTicketBag.WxCardApiTicketResult.expires_in);
                    await UpdateAsync(wxCardApiTicketBag, null).ConfigureAwait(false);
                }
            }
            return wxCardApiTicketBag.WxCardApiTicketResult;
        }
        #endregion
        #endregion
    }
}
