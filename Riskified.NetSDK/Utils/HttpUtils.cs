﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Newtonsoft.Json;
using Riskified.NetSDK.Exceptions;
using Riskified.NetSDK.Logging;

namespace Riskified.NetSDK.Utils
{
    internal enum HttpBodyType
    {
        JSON,
        XML,
        Text
    }

    internal static class HttpUtils
    {
        private const string ShopDomainHeaderName = "X_RISKIFIED_SHOP_DOMAIN";
        private const string SubmitHeaderName = "X_RISKIFIED_SUBMIT_NOW";
        private const string HmacHeaderName = "X-RISKIFIED-HMAC-SHA256";

        private static readonly string AssemblyVersion;

        static HttpUtils()
        {
            // Extracting the product version for later use
            AssemblyVersion = typeof (HttpUtils).Assembly.GetName().Version.ToString();
        }

        /// <summary>
        /// Sends an HTTP Post request to the received url (Riskified server), blocks and waits for response from server
        /// When response is received, Tries to parse its body from JSON to an object of type 'T' which it returns
        /// </summary>
        /// <typeparam name="T">The type of class expected to be received in the response body as JSON</typeparam>
        /// <param name="riskifiedRegistrationWebhookUrl">The full url with internal path of the relevant riskified webhook</param>
        /// <param name="body">The (json) body of the HTTP request</param>
        /// <param name="authToken">The merchant authentication Token</param>
        /// <param name="shopDomain">The shop domain url of the merchant at Riskified</param>
        /// <returns>'T' typed response object</returns>
        public static T JsonPostAndParseResponseToObject<T>(Uri riskifiedRegistrationWebhookUrl, string body, string authToken, string shopDomain,bool isManualSubmit = false) where T : class
        {
            HttpWebResponse response;
            try
            {
                WebRequest request = GeneratePostRequest(riskifiedRegistrationWebhookUrl, body, authToken,shopDomain, HttpBodyType.JSON,isManualSubmit);
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException wex)
            {
                string error = "There was an unknown error sending data to server";
                if (wex.Response != null)
                {
                    HttpWebResponse errorResponse = (HttpWebResponse)wex.Response;
                    try
                    {
                        T errRes = HttpUtils.ParseObjectFromJsonResponse<T>(errorResponse);
                        return errRes;
                    }
                    catch (Exception parseEx)
                    {
                        if (errorResponse.StatusCode == HttpStatusCode.InternalServerError)
                            error = "Server side error (500): ";
                        else if (errorResponse.StatusCode == HttpStatusCode.BadRequest)
                            error = "Client side error (400): ";
                        else
                            error = "Error occurred. Http status code " + errorResponse.StatusCode + ":";
                        error += parseEx.Message;
                    }
                }
                LoggingServices.Error(error, wex);
                throw new RiskifiedTransactionException(error, wex);
            }
            catch (Exception e)
            {
                const string errorMsg = "There was an unknown error connecting to Riskified server";
                LoggingServices.Error(errorMsg, e);
                throw new RiskifiedTransactionException(errorMsg, e);
            }

            T resObj =
                HttpUtils.ParseObjectFromJsonResponse<T>(response);
            return resObj;
        }

        private static string CalcHmac(string data, string authToken)
        {
            byte[] key = Encoding.ASCII.GetBytes(authToken);
            var myhmacsha256 = new HMACSHA256(key);
            byte[] byteArray = Encoding.UTF8.GetBytes(data);
            var stream = new MemoryStream(byteArray);
            string result = myhmacsha256.ComputeHash(stream).Aggregate("", (s, e) => s + String.Format("{0:x2}", e), s => s);
            return result;
        }

        private static WebRequest GeneratePostRequest(Uri url, string body, string authToken,string shopDomain, HttpBodyType bodyType,bool shouldIncludeSubmitHeader = false)
        {
            HttpWebRequest request = WebRequest.CreateHttp(url);
            // Set custom Riskified headers
            AddDefaultHeaders(request.Headers,authToken,shopDomain,body);
            if (shouldIncludeSubmitHeader)
            {
                request.Headers.Add(SubmitHeaderName,"true");
            }
            
            request.Method = "POST";
            request.ContentType = "application/"+ Enum.GetName(typeof(HttpBodyType),bodyType).ToLower();
            request.UserAgent = "Riskified.NetSDK/" + AssemblyVersion;
            request.Accept = "*/*";
            request.AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip;
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            request.ContentLength = bodyBytes.Length;
            Stream bodyStream = request.GetRequestStream();
            bodyStream.Write(bodyBytes, 0, bodyBytes.Length);
            bodyStream.Close();

            return request;
        }

        private static void AddDefaultHeaders(WebHeaderCollection headers, string authToken, string shopDomain, string body)
        {
            string hashCode = CalcHmac(body, authToken);
            headers.Add(HmacHeaderName, hashCode);
            headers.Add(ShopDomainHeaderName, shopDomain);
            // TODO add support for gzip compression for non-sandbox env
            headers.Add("Accept-Encoding", "gzip,deflate,sdch");
        }

        private static T ParseObjectFromJsonResponse<T>(WebResponse response) where T : class
        {
            var bodyStream = response.GetResponseStream();
            string responseBody;
            try
            {
                responseBody = ExtractStreamData(bodyStream);
            }
            finally
            {
                response.Close();
            }

            return JsonStringToObject<T>(responseBody);
        }

        private static T JsonStringToObject<T>(string responseBody) where T : class
        {
            T transactionResult;
            try
            {
                transactionResult = JsonConvert.DeserializeObject<T>(responseBody);
            }
            catch (Exception e)
            {
                string errorMsg =
                    "Unable to parse JSON response body to type: " + typeof(T).Name + ". Body was: " + responseBody;
                LoggingServices.Error(errorMsg, e);
                throw new RiskifiedTransactionException(errorMsg, e);
            }
            return transactionResult;
        }

        public static T ParsePostRequestToObject<T>(HttpListenerRequest request) where T : class
        {
            if (request.HasEntityBody)
            {
                Stream s = request.InputStream;
                string postData = ExtractStreamData(s);
                T obj = JsonStringToObject<T>(postData);
                return obj;
            }
            return null;
        }

        private static string ExtractStreamData(Stream stream)
        {
            if (stream != null)
            {
                // Open the stream using a StreamReader for easy access.
                var reader = new StreamReader(stream);
                // Read the content.
                string streamData = reader.ReadToEnd();
                reader.Close();
                stream.Close();
                /* no need to verify responses
                if (!IsStringVerified(streamData, authToken, hmacValueToVerify))
                {
                    string err = "Data from Riskified server NOT VERIFIED - ignoring it. Body was: " + streamData;
                    LoggingServices.Error(err);
                    throw new RiskifiedTransactionException(err);
                }
                */
                return streamData;
            }
            string errMsg = "Unknown data from Riskified server - ignoring it. Body was null";
            LoggingServices.Error(errMsg);
            throw new RiskifiedTransactionException(errMsg);
        }

        private static bool IsStringVerified(string data, string authToken, string hmacValueToVerify)
        {
            string calculatedHmac = CalcHmac(data, authToken);
            return calculatedHmac.Equals(hmacValueToVerify);
        }

        public static void BuildAndSendResponse(HttpListenerResponse response, string authToken,string shopDomain, string body,bool isActionSucceeded)
        {
            AddDefaultHeaders(response.Headers,authToken,shopDomain,body);
            response.ContentType = "text/html";
            response.ContentEncoding = Encoding.UTF8;
            if (isActionSucceeded)
                response.StatusCode = (int) HttpStatusCode.OK;
            else
                response.StatusCode = (int) HttpStatusCode.BadRequest;

            byte[] buffer = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = buffer.Length;
            Stream output = response.OutputStream;
            output.Write(buffer, 0, buffer.Length);
            output.Close();
        }

        public static Uri BuildUrl(string hostUrl, string relativePath)
        {
            Uri fullUrl = new Uri(new Uri(hostUrl),relativePath);
            return fullUrl;
        }
    }
}
