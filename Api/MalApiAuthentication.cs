#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using jellyfin_ani_sync.Configuration;
using jellyfin_ani_sync.Models;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace jellyfin_ani_sync.Api {
    public class MalApiAuthentication {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _malAuthApiUrl = "https://myanimelist.net/v1/oauth2";
        private readonly string _redirectUrl;
        private readonly ProviderApiAuth _providerApiAuth;
        private readonly string _codeChallenge = "eZBLUX_JPk4~el62z_k3Q4fV5CzCYHoTz4iLKvwJ~9QTsTJNlzwveKCSYCSiSOa5zAm5Zt~cfyVM~3BuO4kQ0iYwCxPoeN0SOmBYR_C.QgnzyYE4KY-xIe4Vy1bf7_B4";

        public MalApiAuthentication(IHttpClientFactory httpClientFactory, IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor, ProviderApiAuth? overrideProviderApiAuth = null, string? overrideRedirectUrl = null) {
            _httpClientFactory = httpClientFactory;
            if (overrideProviderApiAuth != null) {
                _providerApiAuth = overrideProviderApiAuth;
            } else {
                _providerApiAuth = Plugin.Instance?.PluginConfiguration.ProviderApiAuth?.FirstOrDefault(item => item.Name == ApiName.Mal) ?? throw new NullReferenceException("No provider API auth in plugin config");
            }

            var userCallbackUrl = Plugin.Instance.PluginConfiguration.callbackUrl;
            if (overrideRedirectUrl != null && overrideRedirectUrl != "local") {
                _redirectUrl = overrideRedirectUrl + "/AniSync/authCallback";
            } else {
                if (overrideRedirectUrl is "local") {
#if NET5_0
                    _redirectUrl =  serverApplicationHost.ListenWithHttps ? $"https://{httpContextAccessor.HttpContext.Connection.LocalIpAddress}:{serverApplicationHost.HttpsPort}/AniSync/authCallback" : $"http://{httpContextAccessor.HttpContext.Connection.LocalIpAddress}:{serverApplicationHost.HttpPort}/AniSync/authCallback";
#elif NET6_0
                    _redirectUrl = serverApplicationHost.GetApiUrlForLocalAccess() + "/AniSync/authCallback";
#endif
                } else {
#if NET5_0
                    if (userCallbackUrl != null) {
                        _redirectUrl = userCallbackUrl + "/AniSync/authCallback";
                    } else {
                        _redirectUrl = serverApplicationHost.ListenWithHttps ? $"https://{httpContextAccessor.HttpContext.Connection.LocalIpAddress}:{serverApplicationHost.HttpsPort}/AniSync/authCallback" : $"http://{httpContextAccessor.HttpContext.Connection.LocalIpAddress}:{serverApplicationHost.HttpPort}/AniSync/authCallback";
                    }
#elif NET6_0
                    _redirectUrl = userCallbackUrl != null ? userCallbackUrl + "/AniSync/authCallback" : serverApplicationHost.GetApiUrlForLocalAccess() + "/AniSync/authCallback";
#endif
                }
            }
        }

        public string BuildAuthorizeRequestUrl() {
            return $"{_malAuthApiUrl}/authorize?response_type=code&client_id={_providerApiAuth.ClientId}&code_challenge={_codeChallenge}&redirect_uri={_redirectUrl}";
        }

        /// <summary>
        /// Get a new MAL API auth token.
        /// </summary>
        /// <param name="httpClientFactory"></param>
        /// <param name="code">Optional auth code to generate a new token with.</param>
        /// <param name="refreshToken">Optional refresh token to refresh an existing token with.</param>
        public UserApiAuth GetMalToken(Guid userId, string? code = null, string? refreshToken = null) {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);

            FormUrlEncodedContent formUrlEncodedContent;

            if (refreshToken != null) {
                formUrlEncodedContent = new FormUrlEncodedContent(new[] {
                    new KeyValuePair<string, string>("client_id", _providerApiAuth.ClientId),
                    new KeyValuePair<string, string>("client_secret", _providerApiAuth.ClientSecret),
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("refresh_token", refreshToken)
                });
            } else {
                formUrlEncodedContent = new FormUrlEncodedContent(new[] {
                    new KeyValuePair<string, string>("client_id", _providerApiAuth.ClientId),
                    new KeyValuePair<string, string>("client_secret", _providerApiAuth.ClientSecret),
                    new KeyValuePair<string, string>("code", code),
                    new KeyValuePair<string, string>("code_verifier", _codeChallenge),
                    new KeyValuePair<string, string>("grant_type", "authorization_code"),
                    new KeyValuePair<string, string>("redirect_uri", _redirectUrl)
                });
            }

            var response = client.PostAsync(new Uri($"{_malAuthApiUrl}/token"), formUrlEncodedContent).Result;

            if (response.IsSuccessStatusCode) {
                var content = response.Content.ReadAsStream();

                StreamReader streamReader = new StreamReader(content);

                TokenResponse tokenResponse = JsonSerializer.Deserialize<TokenResponse>(streamReader.ReadToEnd());
                
                UserConfig? pluginConfig = Plugin.Instance.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == userId);

                if (pluginConfig != null) {
                    var apiAuth = pluginConfig.UserApiAuth?.FirstOrDefault(item => item.Name == ApiName.Mal);

                    UserApiAuth newUserApiAuth = new UserApiAuth {
                        Name = ApiName.Mal,
                        AccessToken = tokenResponse.access_token,
                        RefreshToken = tokenResponse.refresh_token
                    };

                    if (apiAuth != null) {
                        apiAuth.AccessToken = tokenResponse.access_token;
                        apiAuth.RefreshToken = tokenResponse.refresh_token;
                    } else {
                        pluginConfig.AddUserApiAuth(newUserApiAuth);
                    }

                    Plugin.Instance.SaveConfiguration();
                    return newUserApiAuth;
                }

                throw new NullReferenceException("The user you are attempting to authenticate does not exist in the plugins config file");
            }

            throw new AuthenticationException("Could not retrieve MAL token: " + response.StatusCode + " - " + response.ReasonPhrase);
        }

        public static string GeneratePkce() {
            string characters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";
            var chars = new char[128];
            var random = new Random();

            for (var x = 0; x < chars.Length; x++) {
                chars[x] = characters[random.Next(characters.Length)];
            }

            return new string(chars);
        }
    }

    public class TokenResponse {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
    }
}