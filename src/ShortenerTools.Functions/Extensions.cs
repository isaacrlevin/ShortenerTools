using Azure.AI.OpenAI;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShortenerTools.Functions
{
    public static class Extensions
    {
        public static void AddAIServices(this FunctionsApplicationBuilder builder)
        {
            var inAspire = bool.Parse(Environment.GetEnvironmentVariable("IN_ASPIRE") ?? "False");
            var useOllama = bool.Parse(Environment.GetEnvironmentVariable("USE_OLLAMA") ?? "False");
            var deployment = Environment.GetEnvironmentVariable("DeploymentName") ?? "gpt-4o-mini";

            IChatClient chatClient = null;

            if (inAspire)
            {
                if (useOllama)
                {
                    builder.AddOllamaApiClient("chat")
                        .AddKeyedChatClient("chat");
                    chatClient = builder.Services.BuildServiceProvider().GetRequiredKeyedService<IChatClient>("chat");
                }
                else
                {
                    builder.AddKeyedAzureOpenAIClient("chat");
                    chatClient = builder.Services.BuildServiceProvider().GetRequiredKeyedService<OpenAIClient>("chat").GetChatClient(deployment).AsIChatClient();
                }
            }
            else
            {
                var endpoint = Environment.GetEnvironmentVariable("AzureOpenAIEndpoint");
                var key = Environment.GetEnvironmentVariable("AzureOpenAIKey");

                if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
                {
                    throw new NotImplementedException("AzureOpenAIEndpoint and AzureOpenAIKey must be set in the environment variables");
                }
                else
                {
                    chatClient = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(key)).GetChatClient(deployment).AsIChatClient();
                }
            }

            if (chatClient != null)
            {
                builder.Services.AddChatClient(chatClient)
                      .UseFunctionInvocation()
                      // Prompts, scraped page content and model responses may be private.
                      // Never opt into sensitive content telemetry, including locally.
                      .UseOpenTelemetry(configure: t => t.EnableSensitiveData = false)
                      //.UseLogging()
                      .Build();
            }
        }
    }
}
