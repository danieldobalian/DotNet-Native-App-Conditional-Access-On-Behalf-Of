﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;

using TodoListService.Models;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Globalization;
using System.Configuration;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using System.Web;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using TodoListService.DAL;

namespace TodoListService.Controllers
{
    [Authorize]
    public class AccessCaApiController : ApiController
    {
        //
        // The Client ID is used by the application to uniquely identify itself to Azure AD.
        // The App Key is a credential used by the application to authenticate to Azure AD.
        // The Tenant is the name of the Azure AD tenant in which this application is registered.
        // The AAD Instance is the instance of Azure, for example public Azure or Azure China.
        // The Authority is the sign-in URL of the tenant.
        //
        private static string aadInstance = ConfigurationManager.AppSettings["ida:AADInstance"];
        private static string tenant = ConfigurationManager.AppSettings["ida:Tenant"];
        private static string clientId = ConfigurationManager.AppSettings["ida:ClientId"];
        private static string appKey = ConfigurationManager.AppSettings["ida:AppKey"];

        //
        // To authenticate to the Graph API, the app needs to know the Grah API's App ID URI.
        // To contact the Me endpoint on the Graph API we need the URL as well.
        //
        private static string graphResourceId = ConfigurationManager.AppSettings["ida:GraphResourceId"];
        private static string graphUserUrl = ConfigurationManager.AppSettings["ida:GraphUserUrl"];
        private static string caResourceId = ConfigurationManager.AppSettings["ida:CAProtectedResource"];
        private const string TenantIdClaimType = "http://schemas.microsoft.com/identity/claims/tenantid";

        // GET: api/ConditionalAccess
        public async Task Get()
        {
            if (!ClaimsPrincipal.Current.FindFirst("http://schemas.microsoft.com/identity/claims/scope").Value.Contains("user_impersonation"))
            {
                throw new HttpResponseException(new HttpResponseMessage { StatusCode = HttpStatusCode.Unauthorized, ReasonPhrase = "The Scope claim does not contain 'user_impersonation' or scope claim not found" });
            }

            AuthenticationResult result = null;

            //
            //   Use ADAL to get a token On Behalf Of the current user.  To do this we will need:
            //      The Resource ID of the service we want to call.
            //      The current user's access token, from the current request's authorization header.
            //      The credentials of this application.
            //      The username (UPN or email) of the user calling the API
            //
            ClientCredential clientCred = new ClientCredential(clientId, appKey);
            var bootstrapContext = ClaimsPrincipal.Current.Identities.First().BootstrapContext as System.IdentityModel.Tokens.BootstrapContext;
            string userName = ClaimsPrincipal.Current.FindFirst(ClaimTypes.Upn) != null ? ClaimsPrincipal.Current.FindFirst(ClaimTypes.Upn).Value : ClaimsPrincipal.Current.FindFirst(ClaimTypes.Email).Value;
            string userAccessToken = bootstrapContext.Token;
            UserAssertion userAssertion = new UserAssertion(bootstrapContext.Token, "urn:ietf:params:oauth:grant-type:jwt-bearer", userName);

            string authority = String.Format(CultureInfo.InvariantCulture, aadInstance, tenant);
            string userId = ClaimsPrincipal.Current.FindFirst(ClaimTypes.NameIdentifier).Value;
            AuthenticationContext authContext = new AuthenticationContext(authority, new DbTokenCache(userId));

            // In the case of a transient error, retry once after 1 second, then abandon.
            // Retrying is optional.  It may be better, for your application, to return an error immediately to the user and have the user initiate the retry.
            bool retry = false;
            int retryCount = 0;

            do
            {
                retry = false;
                try
                {
                    result = await authContext.AcquireTokenAsync(caResourceId, clientCred, userAssertion);
                }
                catch (AdalServiceException ex)
                {
                    if (ex.ErrorCode == "interaction_required")
                    {
                        String claims = null;
                        String error = null;

                        // Extracts the error and claims data from exception JSON 
                        String temp = ex.InnerException.InnerException.Message;
                        var output = JsonConvert.DeserializeObject(temp);

                        foreach (var x in (JObject)output)
                        {
                            String jvalue = x.Key;
                            if (jvalue == "claims")
                            {
                                claims = x.Value.ToString();
                            }
                            if (jvalue == "error")
                            {
                                error = x.Value.ToString();
                            }
                        }

                        // Clear the token cache 
                        authContext.TokenCache.Clear();

                        HttpResponseMessage myMessage = new HttpResponseMessage { StatusCode = HttpStatusCode.BadRequest, ReasonPhrase = error, Content = new StringContent(claims) };

                        throw new HttpResponseException(myMessage);
                    }
                }
                catch (AdalException ex)
                {
                    if (ex.ErrorCode == "temporarily_unavailable")
                    {
                        // Transient error, OK to retry.
                        retry = true;
                        retryCount++;
                        Thread.Sleep(1000);
                    }
                }
            } while ((retry == true) && (retryCount < 1));

            // Access token is available in result; 
            String oboAccessToken = result.AccessToken;

            //
            // We can now use this  access token to accesss our Conditional-Access protected Web API using On-behalf-of
            // Use thie code below to call the downstream Web API OBO
            //

            // private HttpClient httpClient = new HttpClient();
            // httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
            // HttpResponseMessage response = await httpClient.GetAsync(WebAPI2HttpEndpoint (App ID URI + "/endpoint");

            return;
        }
    }
}
