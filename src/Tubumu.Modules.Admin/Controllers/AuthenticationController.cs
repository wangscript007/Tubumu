﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tubumu.Modules.Admin.Application.Services;
using Tubumu.Modules.Admin.Models;
using Tubumu.Modules.Admin.Models.Input;
using Tubumu.Modules.Admin.Settings;
using Tubumu.Modules.Framework.Application.Services;
using Tubumu.Modules.Framework.Authorization;
using Tubumu.Modules.Framework.Extensions;
using Tubumu.Modules.Framework.Models;

namespace Tubumu.Modules.Admin.Controllers
{
    /// <summary>
    /// Authentication Controller (用户认证)
    /// </summary>
    [Route("Api/[controller]/[action]")]
    [ApiController]
    [IgnoreAntiforgeryToken]
    [Authorize]
    public class AuthenticationController : ControllerBase
    {
        private readonly AuthenticationSettings _authenticationSettings;
        private readonly IUserService _userService;
        private readonly ITokenService _tokenService;
        private readonly IMobileUserService _mobileUserService;
        private readonly IWeixinUserService _weixinUserService;
        private readonly IUserActionLogService _userActionLogService;
        private readonly IHttpClientFactory _clientFactory;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="authenticationSettingsOptions"></param>
        /// <param name="flashValidationSettingsOptions"></param>
        /// <param name="userService"></param>
        /// <param name="tokenService"></param>
        /// <param name="mobileUserService"></param>
        /// <param name="weixinUserService"></param>
        /// <param name="userActionLogService"></param>
        /// <param name="clientFactory"></param>
        public AuthenticationController(
            IOptions<AuthenticationSettings> authenticationSettingsOptions,
            IUserService userService,
            ITokenService tokenService,
            IMobileUserService mobileUserService,
            IWeixinUserService weixinUserService,
            IUserActionLogService userActionLogService,
            IHttpClientFactory clientFactory
            )
        {
            _authenticationSettings = authenticationSettingsOptions.Value;
            _userService = userService;
            _tokenService = tokenService;
            _mobileUserService = mobileUserService;
            _weixinUserService = weixinUserService;
            _userActionLogService = userActionLogService;
            _clientFactory = clientFactory;
        }

        /// <summary>
        /// 账号(用户名、手机号或邮箱) + 密码 登录
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResultData<ApiResultTokenData>> Login(AccountPasswordLoginInput input)
        {
            var result = new ApiResultData<ApiResultTokenData>();
            var userInfo = await _userService.GetNormalUserAsync(input.Account, input.Password);
            if (userInfo == null)
            {
                result.Code = 400;
                result.Message = "账号或密码错误，或用户状态不允许登录";
                return result;
            }

            await SaveUserActionLogAsync(userInfo.UserId, 1, "账号(用户名、手机号或邮箱) + 密码 登录", input);

            result.Data = await _tokenService.GenerateApiResultTokenData(userInfo);
            result.Code = 200;
            result.Message = "登录成功";
            return result;
        }

        /// <summary>
        /// 注销
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResult> Logout(ClientTypeInput input)
        {
            var userId = HttpContext.User.GetUserId();
            if (userId >= 0)
            {
                await _tokenService.RevokeRefreshTokenAsync(userId);
                await _userService.SignOutAsync(userId);

                await SaveUserActionLogAsync(userId, 2, "注销", input);
            }
            var result = new ApiResult
            {
                Code = 200,
                Message = "注销成功",
            };

            return result;
        }

        /// <summary>
        /// 获取手机验证码
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResult> GetMobileValidationCode(GetMobileValidationCodeInput input)
        {
            var returnResult = new ApiResult();
            var getResult = await _mobileUserService.GetMobileValidationCodeAsync(input, ModelState);
            if (!getResult)
            {
                returnResult.Code = 400;
                returnResult.Message = $"获取手机验证码失败: { ModelState.FirstErrorMessage() }";
            }
            else
            {
                returnResult.Code = 200;
                returnResult.Message = "获取手机验证码成功";
            }
            return returnResult;
        }

        /// <summary>
        /// 手机号 + 验证码 + 密码 注册
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResultData<ApiResultTokenData>> MobilePassswordRegister(MobilePassswordValidationCodeRegisterInput input)
        {
            var returnResult = new ApiResultData<ApiResultTokenData>();
            var verifyMobileValidationCodeInput = new VerifyMobileValidationCodeInput
            {
                Mobile = input.Mobile,
                Type = MobileValidationCodeType.Register, // 注册
                ValidationCode = input.ValidationCode,
            };
            if (!await _mobileUserService.VerifyMobileValidationCodeAsync(verifyMobileValidationCodeInput, ModelState))
            {
                returnResult.Code = 400;
                returnResult.Message = ModelState.FirstErrorMessage();
                return returnResult;
            }
            await _mobileUserService.FinishVerifyMobileValidationCodeAsync(verifyMobileValidationCodeInput.Mobile, verifyMobileValidationCodeInput.Type, ModelState);
            var userInfo = await _mobileUserService.GenerateItemAsync(_authenticationSettings.RegisterDefaultGroupId, _authenticationSettings.RegisterDefaultStatus, input, ModelState);
            if (userInfo == null)
            {
                returnResult.Code = 400;
                returnResult.Message = ModelState.FirstErrorMessage();
                return returnResult;
            }

            if (userInfo.Status != UserStatus.Normal)
            {
                returnResult.Code = 201;
                returnResult.Message = "注册成功，请等待审核。";
                return returnResult;
            }

            returnResult.Data = await _tokenService.GenerateApiResultTokenData(userInfo);
            returnResult.Code = 200;
            returnResult.Message = "注册成功";
            return returnResult;
        }

        /// <summary>
        /// 手机号 + 密码 登录
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResultData<ApiResultTokenData>> MobilePasswordLogin(MobilePasswordLoginInput input)
        {
            var returnResult = new ApiResultData<ApiResultTokenData>();
            var userInfo = await _userService.GetNormalUserAsync(input.Mobile, input.Password);
            if (userInfo == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "手机号或密码错误，请重试。";
                return returnResult;
            }

            await SaveUserActionLogAsync(userInfo.UserId, 1, "手机号 + 密码 登录", input);

            returnResult.Data = await _tokenService.GenerateApiResultTokenData(userInfo);
            returnResult.Code = 200;
            returnResult.Message = "登录成功";
            return returnResult;
        }

        /// <summary>
        /// 手机号 + 验证码 + 新密码 重置密码
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResult> MobileResetPasssword(MobileResetPassswordInput input)
        {
            var returnResult = new ApiResult();
            var verifyMobileValidationCodeInput = new VerifyMobileValidationCodeInput
            {
                Mobile = input.Mobile,
                Type = MobileValidationCodeType.ResetPassword, // 重置密码
                ValidationCode = input.ValidationCode,
            };
            if (!await _mobileUserService.VerifyMobileValidationCodeAsync(verifyMobileValidationCodeInput, ModelState))
            {
                returnResult.Code = 400;
                returnResult.Message = ModelState.FirstErrorMessage();
                return returnResult;
            }
            await _mobileUserService.FinishVerifyMobileValidationCodeAsync(verifyMobileValidationCodeInput.Mobile, verifyMobileValidationCodeInput.Type, ModelState);
            if (!await _mobileUserService.ResetPasswordAsync(input, ModelState))
            {
                returnResult.Code = 400;
                returnResult.Message = ModelState.FirstErrorMessage();
                return returnResult;
            }

            returnResult.Code = 200;
            returnResult.Message = "重置密码成功";
            return returnResult;
        }

        /// <summary>
        /// 手机号 + 验证码 登录
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResultData<ApiResultTokenData>> MobileLogin(MobileValidationCodeLoginInput input)
        {
            var returnResult = new ApiResultData<ApiResultTokenData>();
            var verifyMobileValidationCodeInput = new VerifyMobileValidationCodeInput
            {
                Mobile = input.Mobile,
                Type = MobileValidationCodeType.Login, // 登录
                ValidationCode = input.ValidationCode,
            };
            if (!await _mobileUserService.VerifyMobileValidationCodeAsync(verifyMobileValidationCodeInput, ModelState))
            {
                returnResult.Code = 400;
                returnResult.Message = ModelState.FirstErrorMessage();
                return returnResult;
            }
            var userInfo = await _mobileUserService.GetOrGenerateItemByMobileAsync(_authenticationSettings.RegisterDefaultGroupId,
                _authenticationSettings.RegisterDefaultStatus,
                input.Mobile,
                true,
                ModelState);
            if (userInfo == null)
            {
                returnResult.Code = 400;
                returnResult.Message = ModelState.FirstErrorMessage();
                return returnResult;
            }

            if (userInfo.Status != UserStatus.Normal)
            {
                returnResult.Code = 201;
                returnResult.Message = "注册成功，请等待审核。";
                return returnResult;
            }

            await SaveUserActionLogAsync(userInfo.UserId, 1, "手机号 + 验证码 登录", input);

            returnResult.Data = await _tokenService.GenerateApiResultTokenData(userInfo);
            returnResult.Code = 200;
            returnResult.Message = "登录成功";
            return returnResult;
        }

        /// <summary>
        /// 微信小程序登录
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResultData<ApiResultTokenData>> WeixinAppLogin(WeixinAppLoginInput input)
        {
            var returnResult = new ApiResultData<ApiResultTokenData>();
            var openId = await _weixinUserService.GetWeixinAppOpenIdAsync(input.Code);
            if (openId == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "异常：获取微信 OpenId 失败";
                return returnResult;
            }
            var userInfo = await _weixinUserService.GetOrGenerateItemByWeixinAppOpenIdAsync(_authenticationSettings.RegisterDefaultGroupId,
                _authenticationSettings.RegisterDefaultStatus,
                openId);
            if (userInfo == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "异常：微信小程序登录失败";
                return returnResult;
            }

            if (userInfo.Status != UserStatus.Normal)
            {
                returnResult.Code = 201;
                returnResult.Message = "注册成功，请等待审核。";
                return returnResult;
            }

            await SaveUserActionLogAsync(userInfo.UserId, 1, "微信小程序登录", input);

            returnResult.Data = await _tokenService.GenerateApiResultTokenData(userInfo);
            returnResult.Code = 200;
            returnResult.Message = "登录成功";
            return returnResult;
        }

        /// <summary>
        /// 微信小程序登录, 并获取用户手机号
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResultData<ApiResultTokenData>> WeixinAppLoginWithMobile(WeixinAppLoginWithMobileInput input)
        {
            var returnResult = new ApiResultData<ApiResultTokenData>();
            var userInfo = await _weixinUserService.GetOrGenerateItemByWeixinAppCodeAsync(_authenticationSettings.RegisterDefaultGroupId,
                _authenticationSettings.RegisterDefaultStatus,
                input.Code, 
                input.EncryptedData, 
                input.IV);
            if (userInfo == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "异常：微信小程序登录失败";
                return returnResult;
            }

            if (userInfo.Status != UserStatus.Normal)
            {
                returnResult.Code = 201;
                returnResult.Message = "注册成功，请等待审核。";
                return returnResult;
            }

            await SaveUserActionLogAsync(userInfo.UserId, 1, "微信小程序登录", input);

            returnResult.Data = await _tokenService.GenerateApiResultTokenData(userInfo);
            returnResult.Code = 200;
            returnResult.Message = "登录成功";
            return returnResult;
        }


        /// <summary>
        /// 微信移动端登录
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResultData<ApiResultTokenData>> WeixinMobileEndLogin(WeixinMobileEndLoginInput input)
        {
            var returnResult = new ApiResultData<ApiResultTokenData>();
            var openId = await _weixinUserService.GetWeixinMobileEndOpenIdAsync(input.Code);
            if (openId == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "异常：获取微信 OpenId 失败";
                return returnResult;
            }
            var userInfo = await _weixinUserService.GetOrGenerateItemByWeixinMobileEndOpenIdAsync(_authenticationSettings.RegisterDefaultGroupId,
                _authenticationSettings.RegisterDefaultStatus,
                openId);
            if (userInfo == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "异常：微信移动端登录失败";
                return returnResult;
            }

            if (userInfo.Status != UserStatus.Normal)
            {
                returnResult.Code = 201;
                returnResult.Message = "注册成功，请等待审核。";
                return returnResult;
            }

            await SaveUserActionLogAsync(userInfo.UserId, 1, "微信移动端登录", input);

            returnResult.Data = await _tokenService.GenerateApiResultTokenData(userInfo);
            returnResult.Code = 200;
            returnResult.Message = "登录成功";
            return returnResult;
        }

        /// <summary>
        /// 微信网页登录
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResultData<ApiResultTokenData>> WeixinWebLogin(WeixinWebLoginInput input)
        {
            var returnResult = new ApiResultData<ApiResultTokenData>();
            var openId = await _weixinUserService.GetWeixinWebOpenIdAsync(input.Code);
            if (openId == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "异常：获取微信 OpenId 失败";
                return returnResult;
            }
            var userInfo = await _weixinUserService.GetOrGenerateItemByWeixinWebOpenIdAsync(_authenticationSettings.RegisterDefaultGroupId,
                _authenticationSettings.RegisterDefaultStatus,
                openId);
            if (userInfo == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "异常：微信网页登录失败";
                return returnResult;
            }

            if (userInfo.Status != UserStatus.Normal)
            {
                returnResult.Code = 201;
                returnResult.Message = "注册成功，请等待审核。";
                return returnResult;
            }

            await SaveUserActionLogAsync(userInfo.UserId, 1, "微信网页登录", input);

            returnResult.Data = await _tokenService.GenerateApiResultTokenData(userInfo);
            returnResult.Code = 200;
            returnResult.Message = "登录成功";
            return returnResult;
        }

        /// <summary>
        /// 已登录用户绑定手机号
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ApiResult> BindMobile(MobileValidationCodeLoginInput input)
        {
            var returnResult = new ApiResult();
            var verifyMobileValidationCodeInput = new VerifyMobileValidationCodeInput
            {
                Mobile = input.Mobile,
                Type = MobileValidationCodeType.Bind, // 绑定
                ValidationCode = input.ValidationCode,
            };
            if (!await _mobileUserService.VerifyMobileValidationCodeAsync(verifyMobileValidationCodeInput, ModelState))
            {
                returnResult.Code = 400;
                returnResult.Message = ModelState.FirstErrorMessage();
                return returnResult;
            }
            var bindResult = await _mobileUserService.ChangeMobileAsync(HttpContext.User.GetUserId(),
                input.Mobile,
                true,
                ModelState);
            if (!bindResult)
            {
                returnResult.Code = 400;
                returnResult.Message = ModelState.FirstErrorMessage();
                return returnResult;
            }

            returnResult.Code = 200;
            returnResult.Message = "绑定成功";
            return returnResult;
        }

        /// <summary>
        /// 已登录用户移动端绑定微信
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ApiResult> BindWeixinMobileEnd(WeixinMobileEndLoginInput input)
        {
            var returnResult = new ApiResult();
            var openId = await _weixinUserService.GetWeixinAppOpenIdAsync(input.Code);
            if (openId == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "异常：获取微信 OpenId 失败";
                return returnResult;
            }
            var bindResult = await _weixinUserService.UpdateWeixinMobileEndOpenIdAsync(HttpContext.User.GetUserId(), openId, ModelState);
            if (!bindResult)
            {
                returnResult.Code = 400;
                returnResult.Message = ModelState.FirstErrorMessage();
                return returnResult;
            }

            returnResult.Code = 200;
            returnResult.Message = "绑定成功";
            return returnResult;
        }

        /// <summary>
        /// 修改密码
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ApiResult> ChanagePassword(UserChangePasswordInput input)
        {
            var returnResult = new ApiResult();
            var userId = HttpContext.User.GetUserId();
            var user = await _userService.GetNormalUserAsync(userId, input.CurrentPassword);
            if (user == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "当前密码输入错误，请重试。";
                return returnResult;
            }
            if (!await _userService.ChangePasswordAsync(HttpContext.User.GetUserId(), input.NewPassword, ModelState))
            {
                returnResult.Code = 400;
                returnResult.Message = ModelState.FirstErrorMessage();
                return returnResult;
            }

            returnResult.Code = 200;
            returnResult.Message = "修改密码成功";
            return returnResult;
        }

        /// <summary>
        /// 根据唯一识别码获取 Token
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResultData<ApiResultTokenData>> GetGuestToken(GuestInput input)
        {
            var returnResult = new ApiResultData<ApiResultTokenData>();
            var userInfo = await _userService.GetOrGenerateItemByUniqueIdAsync(_authenticationSettings.RegisterDefaultGroupId,
                _authenticationSettings.RegisterDefaultStatus,
                input.UniqueId);
            if (userInfo == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "异常：根据唯一识别码获取用户失败";
                return returnResult;
            }

            if (userInfo.Status != UserStatus.Normal)
            {
                returnResult.Code = 201;
                returnResult.Message = "注册成功，请等待审核。";
                return returnResult;
            }

            await SaveUserActionLogAsync(userInfo.UserId, 1, "根据唯一识别码获取游客 Token", input);

            returnResult.Data = await _tokenService.GenerateApiResultTokenData(userInfo);
            returnResult.Code = 200;
            returnResult.Message = "根据唯一识别码获取游客 Token 成功";
            return returnResult;
        }

        #region Private Methods

        private Task SaveUserActionLogAsync(int userId, int actionTypeId, string remark, ClientTypeInput input)
        {
            return _userActionLogService.SaveAsync(new UserActionLogInput
            {
                UserId = userId,
                ActionTypeId = actionTypeId,
                ClientTypeId = input.ClientTypeId,
                ClientAgent = input.ClientAgent,
                Remark = remark
            }, ModelState);
        }

        //DES解密
        private static string DesDecrypt(string decryptString, string decryptKey)
        {
            try
            {
                //将字符转换为UTF - 8编码的字节序列
                var rgbKey = Encoding.UTF8.GetBytes(decryptKey.Substring(0, 8));
                var rgbIV = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                var inputByteArray = Convert.FromBase64String(decryptString);
                //用指定的密钥和初始化向量使用CBC模式的DES解密标准解密
                var dCSP = new DESCryptoServiceProvider();
                dCSP.Mode = CipherMode.CBC;
                dCSP.Padding = PaddingMode.PKCS7;
                var mStream = new MemoryStream();
                var cStream = new CryptoStream(mStream, dCSP.CreateDecryptor(rgbKey, rgbIV), CryptoStreamMode.Write);
                cStream.Write(inputByteArray, 0, inputByteArray.Length);
                cStream.FlushFinalBlock();
                return Encoding.UTF8.GetString(mStream.ToArray());
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return "";
            }
        }

        #endregion
    }
}
