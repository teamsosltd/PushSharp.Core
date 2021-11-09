using System;
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PushSharp.Common;
using Microsoft.Extensions.Logging;

namespace PushSharp.Windows
{
    public class WnsAccessTokenManager
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        Task renewAccessTokenTask = null;
        string accessToken = null;
        //HttpClient http;

        public WnsAccessTokenManager (WnsConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger logger)
        {
            //http = new HttpClient ();
            Configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public WnsConfiguration Configuration { get; private set; }

        public async Task<string> GetAccessToken ()
        {
            if (accessToken == null) {
                if (renewAccessTokenTask == null) {
                    _logger.LogInformation("Renewing Access Token");
                    renewAccessTokenTask = RenewAccessToken ();
                    await renewAccessTokenTask;
                } else {
                    _logger.LogInformation("Waiting for access token");
                    await renewAccessTokenTask;
                }
            }

            return accessToken;
        }

        public void InvalidateAccessToken (string currentAccessToken)
        {
            if (accessToken == currentAccessToken)
                accessToken = null;
        }

        async Task RenewAccessToken ()
        {
            _logger.LogInformation("Get access token");

            var p = new Dictionary<string, string> {
                { "grant_type", "client_credentials" },
                { "client_id", Configuration.PackageSecurityIdentifier },
                { "client_secret", Configuration.ClientSecret },
                { "scope", "notify.windows.com" }
            };

            var http = _httpClientFactory.CreateClient();
            var result = await http.PostAsync ("https://login.live.com/accesstoken.srf", new FormUrlEncodedContent (p)).ConfigureAwait(false);
            _logger.LogInformation($"Access token reponse {result.StatusCode} {result.Content?.Headers?.ContentType}");

            var data = await result.Content.ReadAsStringAsync ().ConfigureAwait(false);
            var token = string.Empty;
            var tokenType = string.Empty;

            try {
                var json = JObject.Parse (data);
                token = json.Value<string> ("access_token");
                tokenType = json.Value<string> ("token_type");
                _logger.LogInformation($"Got token type {tokenType}");
            } 
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decode Access Token");
            }

            if (!string.IsNullOrEmpty (token) && !string.IsNullOrEmpty (tokenType)) {
                accessToken = token;
            } else {
                accessToken = null;
                throw new UnauthorizedAccessException ("Could not retrieve access token for the supplied Package Security Identifier (SID) and client secret");
            }
        }
    }
}

