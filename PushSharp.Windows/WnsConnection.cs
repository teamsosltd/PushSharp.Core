using System;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using System.Xml;
using System.Linq;
using System.Net;
using System.Text;
using System.IO;
using PushSharp.Common;
using Microsoft.Extensions.Logging;

namespace PushSharp.Windows
{
    public class WnsServiceConnectionFactory : IServiceConnectionFactory<WnsNotification>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        WnsAccessTokenManager wnsAccessTokenManager;

        public WnsServiceConnectionFactory (WnsConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger logger)
        {
            wnsAccessTokenManager = new WnsAccessTokenManager (configuration, httpClientFactory, logger);
            Configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public WnsConfiguration Configuration { get; private set; }

        public IServiceConnection<WnsNotification> Create()
        {
            return new WnsServiceConnection (Configuration, wnsAccessTokenManager, _httpClientFactory, _logger);
        }
    }

    public class WnsServiceBroker : ServiceBroker<WnsNotification>
    {
        public WnsServiceBroker (WnsConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<ServiceBroker<WnsNotification>> logger) 
            : base (new WnsServiceConnectionFactory (configuration, httpClientFactory, logger), logger)
        {
        }
    }

    public class WnsServiceConnection : IServiceConnection<WnsNotification>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;

        public WnsServiceConnection (WnsConfiguration configuration, WnsAccessTokenManager accessTokenManager, IHttpClientFactory httpClientFactory, ILogger logger)
        {
            AccessTokenManager = accessTokenManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            Configuration = configuration;
        }

        public WnsAccessTokenManager AccessTokenManager { get; private set; }
        public WnsConfiguration Configuration { get; private set; }

        public async Task Send (WnsNotification notification)
        {
            _logger.LogDebug($"Start Send notification, type={notification.Type}, uri={notification.ChannelUri}");

            try
            {
                // Get or renew our access token
                var accessToken = await AccessTokenManager.GetAccessToken().ConfigureAwait(false);

                //https://cloud.notify.windows.com/?token=.....
                //Authorization: Bearer {AccessToken}
                //

                // Not sure how to do this in httpclient
                //var http = _httpClientFactory.CreateClient();

                using var req = new HttpRequestMessage(HttpMethod.Post, notification.ChannelUri);

                req.Headers.ExpectContinue = false; //Disable expect-100 to improve latency

                req.Headers.TryAddWithoutValidation("X-WNS-Type", string.Format("wns/{0}", notification.Type.ToString().ToLower()));

                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                //if(!http.DefaultRequestHeaders.Contains("Authorization")) //prevent double values
                //    http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

                if (notification.RequestForStatus.HasValue)
                    req.Headers.TryAddWithoutValidation("X-WNS-RequestForStatus", notification.RequestForStatus.Value.ToString().ToLower());

                if (notification.TimeToLive.HasValue)
                    req.Headers.TryAddWithoutValidation("X-WNS-TTL", notification.TimeToLive.Value.ToString()); //Time to live in seconds

                if (notification.Type == WnsNotificationType.Tile)
                {
                    var winTileNot = notification as WnsTileNotification;

                    if (winTileNot != null && winTileNot.CachePolicy.HasValue)
                        req.Headers.Add("X-WNS-Cache-Policy", winTileNot.CachePolicy == WnsNotificationCachePolicyType.Cache ? "cache" : "no-cache");

                    if (winTileNot != null && !string.IsNullOrEmpty(winTileNot.NotificationTag))
                        req.Headers.Add("X-WNS-Tag", winTileNot.NotificationTag); // TILE only
                }
                else if (notification.Type == WnsNotificationType.Badge)
                {
                    var winTileBadge = notification as WnsBadgeNotification;

                    if (winTileBadge != null && winTileBadge.CachePolicy.HasValue)
                        req.Headers.Add("X-WNS-Cache-Policy", winTileBadge.CachePolicy == WnsNotificationCachePolicyType.Cache ? "cache" : "no-cache");
                }
                else if (notification.Type == WnsNotificationType.Toast)
                {
                    req.Headers.TryAddWithoutValidation("X-WindowsPhone-Target", notification.Type.ToString().ToLower());
                }

                if (notification.Type == WnsNotificationType.Raw)
                {
                    req.Content = new StreamContent(new MemoryStream(((WnsRawNotification)notification).RawData));
                }
                else
                {
                    req.Content = new StringContent(
                        notification.Payload.ToString(), // Get XML payload 
                        Encoding.UTF8,
                        "text/xml");
                }

                var result = await _httpClientFactory.CreateClient().SendAsync(req).ConfigureAwait(false);

                var status = ParseStatus(result, notification);

                _logger.LogDebug($"Result: {status}");

                //RESPONSE HEADERS
                // X-WNS-Debug-Trace   string
                // X-WNS-DeviceConnectionStatus  connected | disconnected | tempdisconnected   (if RequestForStatus was set to true)
                // X-WNS-Error-Description  string
                // X-WNS-Msg-ID   string  (max 16 char)
                // X-WNS-NotificationStatus   received | dropped | channelthrottled
                //      

                // 200 OK
                // 400  One or more headers were specified incorrectly or conflict with another header.
                // 401  The cloud service did not present a valid authentication ticket. The OAuth ticket may be invalid.
                // 403  The cloud service is not authorized to send a notification to this URI even though they are authenticated.
                // 404  The channel URI is not valid or is not recognized by WNS. - Raise Expiry
                // 405  Invalid Method - never will get
                // 406  The cloud service exceeded its throttle limit.
                // 410  The channel expired. - Raise Expiry
                // 413  The notification payload exceeds the 5000 byte size limit.
                // 500  An internal failure caused notification delivery to fail.
                // 503  The server is currently unavailable.

                // OK, everything worked!
                if (status.HttpStatus == HttpStatusCode.OK
                    && status.NotificationStatus == WnsNotificationSendStatus.Received)
                {
                    return;
                }

                //401
                if (status.HttpStatus == HttpStatusCode.Unauthorized)
                {
                    AccessTokenManager.InvalidateAccessToken(accessToken);
                    throw new RetryAfterException(notification, "Access token expired", DateTime.UtcNow.AddSeconds(5));
                }

                //404 or 410
                if (status.HttpStatus == HttpStatusCode.NotFound || status.HttpStatus == HttpStatusCode.Gone)
                {
                    throw new DeviceSubscriptionExpiredException(notification)
                    {
                        OldSubscriptionId = notification.ChannelUri,
                        ExpiredAt = DateTime.UtcNow
                    };
                }


                // Any other error
                throw new WnsNotificationException(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed Send notification");
                throw;
            }
        }

        WnsNotificationStatus ParseStatus(HttpResponseMessage resp, WnsNotification notification)
        {
            var result = new WnsNotificationStatus();

            result.Notification = notification;
            result.HttpStatus = resp.StatusCode;

            var wnsDebugTrace = TryGetHeaderValue (resp.Headers, "X-WNS-DEBUG-TRACE") ?? "";
            var wnsDeviceConnectionStatus = TryGetHeaderValue (resp.Headers, "X-WNS-DEVICECONNECTIONSTATUS") ?? "connected";
            var wnsErrorDescription = TryGetHeaderValue (resp.Headers, "X-WNS-ERROR-DESCRIPTION") ?? "";
            var wnsMsgId = TryGetHeaderValue (resp.Headers, "X-WNS-MSG-ID");
            var wnsNotificationStatus = TryGetHeaderValue (resp.Headers, "X-WNS-NOTIFICATIONSTATUS") ?? TryGetHeaderValue(resp.Headers, "X-NOTIFICATIONSTATUS") ?? "" ;

            result.DebugTrace = wnsDebugTrace;
            result.ErrorDescription = wnsErrorDescription;
            result.MessageId = wnsMsgId;

            if (wnsNotificationStatus.Equals("received", StringComparison.InvariantCultureIgnoreCase))
                result.NotificationStatus = WnsNotificationSendStatus.Received;
            else if (wnsNotificationStatus.Equals("dropped", StringComparison.InvariantCultureIgnoreCase))
                result.NotificationStatus = WnsNotificationSendStatus.Dropped;
            else
                result.NotificationStatus = WnsNotificationSendStatus.ChannelThrottled;

            if (wnsDeviceConnectionStatus.Equals("connected", StringComparison.InvariantCultureIgnoreCase))
                result.DeviceConnectionStatus = WnsDeviceConnectionStatus.Connected;
            else if (wnsDeviceConnectionStatus.Equals("tempdisconnected", StringComparison.InvariantCultureIgnoreCase))
                result.DeviceConnectionStatus = WnsDeviceConnectionStatus.TempDisconnected;
            else
                result.DeviceConnectionStatus = WnsDeviceConnectionStatus.Disconnected;

            return result;
        }

        string TryGetHeaderValue (HttpResponseHeaders headers, string headerName)
        {
            IEnumerable<string> values;
            if (headers.TryGetValues (headerName, out values))
                return values.FirstOrDefault ();

            return null;
        }
    }
}

