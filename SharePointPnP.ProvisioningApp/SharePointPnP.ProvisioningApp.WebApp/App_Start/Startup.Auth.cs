﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IdentityModel.Claims;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.OpenIdConnect;
using Owin;
using Newtonsoft.Json;
using SharePointPnP.ProvisioningApp.Infrastructure;
using SharePointPnP.ProvisioningApp.Infrastructure.ADAL;
using System.Net.Http;

namespace SharePointPnP.ProvisioningApp.WebApp
{
    public partial class Startup
    {
        private static string clientId = ConfigurationManager.AppSettings["ida:ClientId"];
        private string appKey = ConfigurationManager.AppSettings["ida:ClientSecret"];
        private string graphResourceID = "https://graph.windows.net";
        private static string aadInstance = EnsureTrailingSlash(ConfigurationManager.AppSettings["ida:AADInstance"]);
        private string authority = aadInstance + "common";

        public void ConfigureAuth(IAppBuilder app)
        {
            
            app.SetDefaultSignInAsAuthenticationType(CookieAuthenticationDefaults.AuthenticationType);

            app.UseCookieAuthentication(new CookieAuthenticationOptions { });

            app.UseOpenIdConnectAuthentication(
                new OpenIdConnectAuthenticationOptions
                {
                    ClientId = clientId,
                    Authority = authority,
                    TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                    {
                        // instead of using the default validation (validating against a single issuer value, as we do in line of business apps), 
                        // we inject our own multitenant validation logic
                        ValidateIssuer = false,
                    },
                    Notifications = new OpenIdConnectAuthenticationNotifications()
                    {
                        SecurityTokenValidated = (context) => 
                        {
                            return Task.FromResult(0);
                        },
                        AuthorizationCodeReceived = async (context) =>
                        {
                            var code = context.Code;

                            // We need to retrieve the RefreshToken manually
                            using (var client = new HttpClient())
                            {
                                // Prepare the AAD OAuth request URI
                                var tokenUri = new Uri($"{authority}/oauth2/token");

                                // Prepare the OAuth 2.0 request for an Access Token with Authorization Code
                                var content = new FormUrlEncodedContent(new[]
                                {
                                    new KeyValuePair<string, string>("grant_type", "authorization_code"),
                                    new KeyValuePair<string, string>("redirect_uri", ConfigurationManager.AppSettings["ida:AppUrl"]),
                                    new KeyValuePair<string, string>("client_id", clientId),
                                    new KeyValuePair<string, string>("client_secret", appKey),
                                    new KeyValuePair<string, string>("code", code),
                                    new KeyValuePair<string, string>("resource", graphResourceID),
                                });

                                // Make the HTTP request
                                var result = await client.PostAsync(tokenUri, content);
                                string jsonToken = await result.Content.ReadAsStringAsync();

                                // Get back the OAuth 2.0 response
                                var token = JsonConvert.DeserializeObject<OAuthTokenResponse>(jsonToken);

                                // Retrieve and deserialize into a JWT token the Access Token
                                var jwtAccessToken = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(token.AccessToken);

                                // Read the currently connected User Principal Name (UPN)
                                var upnClaim = jwtAccessToken.Claims.FirstOrDefault(c => c.Type == "upn");
                                if (upnClaim != null && !String.IsNullOrEmpty(upnClaim.Value))
                                {
                                    System.Threading.Thread.CurrentPrincipal = new System.Security.Claims.ClaimsPrincipal(
                                        new System.Security.Claims.ClaimsIdentity(jwtAccessToken.Claims));
                                }

                                // Read the currently connected Tenant ID (TID)
                                var tenandIdClaim = jwtAccessToken.Claims.FirstOrDefault(c => c.Type == "tid");

                                // Store the Refresh Token in the Azure Key Vault
                                if (tenandIdClaim != null && !String.IsNullOrEmpty(tenandIdClaim.Value))
                                {
                                    await ProvisioningAppManager.AccessTokenProvider.WriteRefreshTokenAsync($"{tenandIdClaim.Value}-{upnClaim.Value.GetHashCode()}", token.RefreshToken);
                                }
                            }
                        },
                        AuthenticationFailed = (context) =>
                        {
                            context.OwinContext.Response.Redirect("/Home/Error");
                            context.HandleResponse(); // Suppress the exception
                            return Task.FromResult(0);
                        }
                    }
                });

        }

        private static string EnsureTrailingSlash(string value)
        {
            if (value == null)
            {
                value = string.Empty;
            }

            if (!value.EndsWith("/", StringComparison.Ordinal))
            {
                return value + "/";
            }

            return value;
        }
    }
}
