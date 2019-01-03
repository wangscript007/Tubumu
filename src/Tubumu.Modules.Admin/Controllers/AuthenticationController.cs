﻿using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Tubumu.Modules.Admin.Frontend;
using Tubumu.Modules.Admin.Models;
using Tubumu.Modules.Admin.Models.Input;
using Tubumu.Modules.Admin.ModuleMenus;
using Tubumu.Modules.Admin.Services;
using Tubumu.Modules.Admin.Settings;
using Tubumu.Modules.Framework.Extensions;
using Tubumu.Modules.Framework.Authorization;
using Tubumu.Modules.Framework.Models;
using Tubumu.Modules.Framework.Swagger;
using Senparc.Weixin.Open;

namespace Tubumu.Modules.Admin.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    [IgnoreAntiforgeryToken]
    [Authorize]
    public class AuthenticationController : ControllerBase
    {
        private readonly JwtSecurityTokenHandler _tokenHandler = new JwtSecurityTokenHandler();
        private readonly AuthenticationSettings _authenticationSettings;
        private readonly TokenValidationSettings _tokenValidationSettings;
        private readonly IUserService _userService;
        private readonly IMobileUserService _mobileUserService;
        private readonly IWeixinUserService _weixinUserService;

        public AuthenticationController(
            IOptions<AuthenticationSettings> authenticationSettingsOptions,
            TokenValidationSettings tokenValidationSettings,
            IUserService userService,
            IMobileUserService mobileUserService,
            IWeixinUserService weixinUserService
            )
        {
            _authenticationSettings = authenticationSettingsOptions.Value;
            _tokenValidationSettings = tokenValidationSettings;
            _userService = userService;
            _mobileUserService = mobileUserService;
            _weixinUserService = weixinUserService;
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
                returnResult.Message = $"获取手机验证码失败: {ModelState.FirstErrorMessage()}";
            }
            else
            {
                returnResult.Code = 200;
                returnResult.Message = "获取手机验证码成功";
            };
            return returnResult;
        }

        /// <summary>
        /// 手机号 + 验证码 + 密码 注册
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResult> MobilePassswordRegister(MobilePassswordValidationCodeRegisterInput input)
        {
            var returnResult = new ApiTokenResult();
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

            var jwt = GetJwt(userInfo);
            returnResult.Token = jwt;
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
        public async Task<ApiResult> MobilePasswordLogin(MobilePasswordLoginInput input)
        {
            var returnResult = new ApiTokenResult();
            var userInfo = await _userService.GetNormalUserAsync(input.Mobile, input.Password);
            if (userInfo == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "手机号或密码错误，请重试。";
                return returnResult;
            }

            var jwt = GetJwt(userInfo);
            returnResult.Token = jwt;
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
        public async Task<ApiResult> MobileLogin(MobileValidationCodeLoginInput input)
        {
            var returnResult = new ApiTokenResult();
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

            var jwt = GetJwt(userInfo);
            returnResult.Token = jwt;
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
        public async Task<ApiResult> WeixinAppLogin(WeixinAppLoginInput input)
        {
            var returnResult = new ApiTokenResult();
            var openId = await _weixinUserService.GetWeixinAppOpenIdAsync(input.Code);
            if(openId == null)
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

            var jwt = GetJwt(userInfo);
            returnResult.Token = jwt;
            returnResult.Code = 200;
            returnResult.Message = "登录成功";
            return returnResult;
        }

        /// <summary>
        /// 微信手机登录
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        [AllowAnonymous]
        public async Task<ApiResult> WeixinMobileEndLogin(WeixinMobileEndLoginInput input)
        {
            var returnResult = new ApiTokenResult();
            var openId = await _weixinUserService.GetWeixinMobileEndOpenIdAsync(input.Code);
            if(openId == null)
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
                returnResult.Message = "异常：微信小程序登录失败";
                return returnResult;
            }

            if (userInfo.Status != UserStatus.Normal)
            {
                returnResult.Code = 201;
                returnResult.Message = "注册成功，请等待审核。";
                return returnResult;
            }

            var jwt = GetJwt(userInfo);
            returnResult.Token = jwt;
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
        public async Task<ApiResult> WeixinWebLogin(WeixinWebLoginInput input)
        {
            var returnResult = new ApiTokenResult();
            var openId = await _weixinUserService.GetWeixinWebOpenIdAsync(input.Code);
            if(openId == null)
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
                returnResult.Message = "异常：微信小程序登录失败";
                return returnResult;
            }

            if (userInfo.Status != UserStatus.Normal)
            {
                returnResult.Code = 201;
                returnResult.Message = "注册成功，请等待审核。";
                return returnResult;
            }

            var jwt = GetJwt(userInfo);
            returnResult.Token = jwt;
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
            if(!bindResult)
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
        /// 已登录用户绑定手机微信
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ApiResult> BindWeixinMobileEnd(WeixinMobileEndLoginInput input)
        {
            var returnResult = new ApiTokenResult();
            var openId = await _weixinUserService.GetWeixinAppOpenIdAsync(input.Code);
            if(openId == null)
            {
                returnResult.Code = 400;
                returnResult.Message = "异常：获取微信 OpenId 失败";
                return returnResult;
            }
            var bindResult = await _weixinUserService.UpdateWeixinMobileEndOpenIdAsync(HttpContext.User.GetUserId(), openId, ModelState);
            if(!bindResult)
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

        #region Private Methods

        private string GetJwt(UserInfo user)
        {
            var groups = from m in user.AllGroups select new Claim(TubumuClaimTypes.Group, m.Name);
            var roles = from m in user.AllRoles select new Claim(ClaimTypes.Role, m.Name);
            var permissions = from m in user.AllPermissions select new Claim(TubumuClaimTypes.Permission, m.Name);
            var claims = (new[] { new Claim(ClaimTypes.Name, user.UserId.ToString()) }).
                Union(groups).
                Union(roles).
                Union(permissions);
            var token = new JwtSecurityToken(
                _tokenValidationSettings.ValidIssuer,
                _tokenValidationSettings.ValidAudience,
                claims,
                expires: DateTime.UtcNow.AddDays(30),
                signingCredentials: SignatureHelper.GenerateSigningCredentials(_tokenValidationSettings.IssuerSigningKey));

            var jwt = _tokenHandler.WriteToken(token);
            return jwt;
        }

        #endregion
    }
}
