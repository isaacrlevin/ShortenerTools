using ShortenerTools.Core.Domain;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Collections.Specialized;



namespace ShortenerTools
{
    public static class Utility
    {

        private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

        //reshuffled for randomisation, same unique characters just jumbled up, you can replace with your own version
        private static readonly int Base = Alphabet.Length;
        //sets the length of the unique code to add to vanity
        private const int MinVanityCodeLength = 5;

        public static async Task<string> GetValidEndUrl(string vanity, StorageTableHelper stgHelper)
        {
            if (string.IsNullOrEmpty(vanity))
            {
                var newKey = await stgHelper.GetNextTableId();
                string getCode() => Encode(newKey, string.Empty);
                if (await stgHelper.IfShortUrlEntityExistByVanity(getCode()))
                    return await GetValidEndUrl(vanity, stgHelper);

                return string.Join(string.Empty, getCode());
            }
            else
            {
                return string.Join(string.Empty, vanity);
            }
        }

        public static string Encode(int i, string shortCode)
        {
            if (string.IsNullOrEmpty(shortCode))
            {
                if (i == 0)
                    return Alphabet[0].ToString();
                var s = string.Empty;
                while (i > 0)
                {
                    s += Alphabet[i % Base];
                    i = i / Base;
                }

                return string.Join(string.Empty, s.Reverse());
            }
            else
            {
                return string.Join(string.Empty, shortCode);
            }
        }

        public static string GetShortUrl(string host, string vanity)
        {
            return host + "/" + vanity;
        }

        public static string AsPage(this Uri uri, Func<string, NameValueCollection> parseQuery)
        {
            var pageUrl = new UriBuilder(uri)
            {
                Port = -1
            };
            var parameters = parseQuery(pageUrl.Query);

            pageUrl.Query = parameters.ToString();
            return $"{pageUrl.Host}{pageUrl.Path}{pageUrl.Query}{pageUrl.Fragment}";
        }
    }
}