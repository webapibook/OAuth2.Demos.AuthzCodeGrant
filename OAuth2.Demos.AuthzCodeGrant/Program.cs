using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Http;
using System.Web.Http.SelfHost;

namespace OAuth2.Demos.AuthzCodeGrant
{
    static class Config
    {
        // TODO Set the client_id and client_secret with your client settings
        public static readonly ClientRegistration Client = new ClientRegistration
        {
            client_id = null,
            client_secret = null,

            // HACK to simplify the hosting, we can use an HTTP URI.
			// Note: You will likely need to add a HTTP reservation for the callback
			// URL from an elevated cmd/PowerShell prompt: https://msdn.microsoft.com/en-us/library/ms733768(v=vs.110).aspx
            // In production it should always be HTTPS
            redirect_uri = "http://localhost/callback"
        };

        public static readonly AuthorizationServer AuthzServer = new AuthorizationServer
        {
            AuthorizationEndpoint = "https://github.com/login/oauth/authorize",
            TokenEndpoint = "https://github.com/login/oauth/access_token",
            UseAuthorizationHeader = false,
        };

        public static readonly Resource ExampleResource = new Resource
        {
            Uri = "https://api.github.com/user",
            Scope = string.Empty // No scope == read-only public info. List of available scopes: https://developer.github.com/v3/oauth/#scopes
        };
    }

    internal class Program
    {
        private static void Main(string[] args)
        {
            Log.Info("Hi, welcome to the OAuth 2.0 Authorization Code Grant Demo");
            if (Config.Client.client_id == null || Config.Client.client_secret == null)
            {
                Log.Warn(
                    "The client_id or the client_secret are not configured."
                    + " Please register a client application and set these properties on the Config class");
                Log.Warn("E.g. for a GitHub client, go to 'Account settings/OAUTH Application', on the GitHub site");
                return;
            }
			
            Log.Info("Creating a self-hosted server to handle the authorization callback...");
            var config = new HttpSelfHostConfiguration(Config.Client.redirect_uri);
            config.Routes.MapHttpRoute(
                "Default",
                "",
                new { controller = "OAuth2Callback" }
                );
			
			// If you get errors here, see note above re. adding HTTP reservation!	
            var server = new HttpSelfHostServer(config);
            server.OpenAsync().Wait();
            Log.Info("Done. Server is opened, the OAuth 2.0 dance can now start");

            Log.Info("First, lets do an authorization request via the User's browser (a new browser tab will be opened)");
            var authzRequest = new AuthorizationRequest
                                   {
                                       client_id = Config.Client.client_id,
                                       response_type = "code",
                                       scope = Config.ExampleResource.Scope,
                                       redirect_uri = Config.Client.redirect_uri,
                                       state = 128.RandomBits()
                                   };
            Db.State = authzRequest.state;

            var uri = new UriBuilder(Config.AuthzServer.AuthorizationEndpoint)
                          {
                              Query = authzRequest.ToPairs().ToQueryString()
                          }.ToString();
            Log.Info("Redirecting browser to {0} ...", uri);
            Process.Start(uri);
            Log.Info("And the dance has begun. Please look at the new browser tab");
            Log.Info("The next time we met it will be on the callback handler\n");

            Console.ReadKey();
            server.CloseAsync().Wait();
            Log.Info("Server is now closed, bye");
        }
    }

    public class OAuth2CallbackController : ApiController
    {
        public HttpResponseMessage Get([FromUri] AuthorizationResponse authzResponse)
        {
            Log.Info("Great, just received the response from the Authorization Endpoint, lets look at it...");
            try
            {
                if (!string.IsNullOrEmpty(authzResponse.error))
                {
                    return Error("Unfortunately the request was not sucessfull. The returned error is {0}, ending",
                                 authzResponse.error);
                }
                Log.Info("Good, the request was sucessfull. Lets see if the returned state matches the sent state...");

                if (authzResponse.state != Db.State)
                {
                    return Error("Hum, the returned state does not match the send state. Ignoring the response, sorry.");
                }

                Log.Info(
                    "Nice, the state matches. Lets now exchange the code for an access token using the token endpoint ...");
                using (var client = new HttpClient())
                {
                    var tokenRequest = new TokenRequest
                                           {
                                               code = authzResponse.code,
                                               grant_type = "authorization_code",
                                               redirect_uri = Config.Client.redirect_uri,
                                           };

                    // Just because GitHub requires it (but the OAuth 2.0 RFC does not)
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    if (Config.AuthzServer.UseAuthorizationHeader)
                    {
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                            Convert.ToBase64String(
                                Encoding.ASCII.GetBytes(Config.Client.client_id + ":" + Config.Client.client_secret)));
                    }
                    else
                    {
                        tokenRequest.client_id = Config.Client.client_id;
                        tokenRequest.client_secret = Config.Client.client_secret;
                    }

                    var resp = client.PostAsync(Config.AuthzServer.TokenEndpoint,
                                                new FormUrlEncodedContent(tokenRequest.ToPairs())).Result;

                    if (resp.StatusCode == HttpStatusCode.InternalServerError)
                    {
                        return Error("Apparently we broke the authorization server, ending.");
                    }
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        return Error("Something is missing, ending.");
                    }
                    if (resp.StatusCode == HttpStatusCode.BadRequest)
                    {
                        return Error("The token endpoint refused to return an acess token, ending.");
                    }
                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        return Error("The token endpoint did't accepted our client credentials, ending.");
                    }
                    if (resp.Content.Headers.ContentType.MediaType != "application/json")
                    {
                        return
                            Error(
                                "Expection 'application/json' from the token endpoint, however it returned '{0}', ending.",
                                resp.Content.Headers.ContentType.MediaType);
                    }

                    var tokenResp = resp.Content.ReadAsAsync<TokenResponse>().Result;
                    if (!tokenResp.token_type.Equals("Bearer", StringComparison.InvariantCultureIgnoreCase))
                    {
                        return Error("Unfortunately, the returned token type is unknown, ending");
                    }
                    var theAccessToken = tokenResp.access_token;
                    Log.Info("Great, we have an access token {0}. Lets use it to GET the resource representation",
                             theAccessToken);
                    using (var resourceClient = new HttpClient())
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, Config.ExampleResource.Uri);
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", theAccessToken);
                        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OAuth2DemosAuthzCodeGrant","1.0"));
                        var resourceResp = resourceClient.SendAsync(request).Result;
                        Log.Info("Returning the resource server response, I hope you liked this demo ...");
                        return resourceResp;
                    }
                }
            }
            catch (Exception e)
            {
                return Error("An unexpected exception just happened, sorry: {0}", e.Message);
            }
        }

        private static HttpResponseMessage Error(string format, params object[] args)
        {
            var msg = string.Format(format, args);
            Log.Warn(msg);
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(msg)
            };
        }
    }

    // The request message sent by the Client, via the User's, to the Authorization Endpoint
    public class AuthorizationRequest
    {
        public string client_id { get; set; }
        public string scope { get; set; }
        public string state { get; set; }
        public string response_type { get; set; }
        public string redirect_uri { get; set; }
    }

    // The response message returned by the Authorization Endpoint
    public class AuthorizationResponse
    {
        public string code { get; set; }
        public string state { get; set; }
        public string error { get; set; }
        public string error_description { get; set; }
    }

    // The request message sent by the Client directly to the Token Endpoint,
    // in order to exchange the authorization code for an access token
    public class TokenRequest
    {
        public string grant_type { get; set; }
        public string code { get; set; }
        public string redirect_uri { get; set; }

        public string client_id { get; set; }
        public string client_secret { get; set; }
    }

    // The response message returned by the Token Endpoint
    public class TokenResponse
    {
        public string access_token { get; set; }
        public string token_type { get; set; }
        public string expires_in { get; set; }

        public string error { get; set; }
    }

    // The characterization of an Authorization Server
    public class AuthorizationServer
    {
        public string AuthorizationEndpoint { get; set; }
        public string TokenEndpoint { get; set; }
        public bool UseAuthorizationHeader { get; set; }
    }

    // The characterization of a Client
    public class ClientRegistration
    {
        public string client_id { get; set; }
        public string client_secret { get; set; }
        public string redirect_uri { get; set; }
    }

    // The characterization of a Resource
    public class Resource
    {
        public string Uri { get; set; }
        public string Scope { get; set; }
    }

    // Just a DB mock for holding the authorization request state
    public static class Db
    {
        public static string State { get; set; }
    }

    public static class Extensions
    {
        public static IEnumerable<KeyValuePair<string, string>> ToPairs(this object obj)
        {
            return obj.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Where(p => p.GetValue(obj) != null)
                .Select(p => new KeyValuePair<string, string>(p.Name, p.GetValue(obj).ToString()));
        }

        public static string ToQueryString(this IEnumerable<KeyValuePair<string, string>> pairs)
        {
            return pairs.Aggregate(new StringBuilder(),
                                   (sb, p) => sb.AppendFormat("{0}={1}&",
                                       HttpUtility.UrlEncode(p.Key),
                                       HttpUtility.UrlEncode(p.Value))).ToString();
        }

        public static string RandomBits(this int size)
        {
            using (var rg = RandomNumberGenerator.Create())
            {
                var bytes = new byte[(size + 7) / 8];
                rg.GetBytes(bytes);
                return Convert.ToBase64String(bytes);
            }
        }
    }

    static class Log
    {
        static Log()
        {
            //Trace.Listeners.Add(new ConsoleTraceListener());
        }

        public static void Info(string format, params object[] args)
        {
            //Trace.TraceInformation(format, args);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(format, args);
            Console.ResetColor();
        }

        public static void Warn(string format, params object[] args)
        {
            //Trace.TraceWarning(format, args);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(format, args);
            Console.ResetColor();
        }
    }
}
