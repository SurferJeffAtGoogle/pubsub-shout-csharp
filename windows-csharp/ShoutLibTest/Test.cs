// Copyright(c) 2016 Google Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not
// use this file except in compliance with the License. You may obtain a copy of
// the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
// WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
// License for the specific language governing permissions and limitations under
// the License.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using ShoutLib;
using Google.Apis.Pubsub.v1;
using Google.Apis.Pubsub.v1.Data;
using Microsoft.Practices.EnterpriseLibrary.Logging;
using Microsoft.Practices.EnterpriseLibrary.Logging.TraceListeners;
using Microsoft.Practices.EnterpriseLibrary.Logging.Formatters;
using System.IO;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;

namespace ShoutLibTest
{
    public class MockMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; }

        protected override Task<HttpResponseMessage> 
            SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Handler == null ? Task.FromResult(new HttpResponseMessage())
                : Task.FromResult(Handler(request));
        }
    }

    public class Test
    {
        private readonly PubsubService _pubsub;
        private readonly string _topicPath;
        private readonly string _subscriptionPath;
        private readonly Shouter _shouter;
        private readonly MemoryStream _memStream;
        private static bool _createdTopicAndSubscription = false;
        private readonly MockMessageHandler _httpMessageHandler;

        public Test()
        {
            _httpMessageHandler = new MockMessageHandler();
            _memStream = new MemoryStream();
            var init = Shouter.Initializer.CreateDefault();
            init.LogWriter = CreateLogWriter(_memStream);
            init.HttpClient = new HttpClient(_httpMessageHandler);
            _pubsub = init.PubsubService;
            init.SubscriptionName += "-test";
            init.ProjectId = Environment.GetEnvironmentVariable("GOOGLE_PROJECT_ID");
            _topicPath = $"projects/{init.ProjectId}/topics/{init.SubscriptionName}-topic";
            _subscriptionPath = $"projects/{init.ProjectId}/subscriptions/{init.SubscriptionName}";
            CreateTopicAndSubscription();
            ClearSubscription();
            _shouter = new Shouter(init);
        }

        private static LogWriter CreateLogWriter(MemoryStream memStream)
        {
            var config = new LoggingConfiguration();
            var source = config.AddLogSource(
                "All", System.Diagnostics.SourceLevels.All, true);
            source.AddAsynchronousTraceListener(new FormattedTextWriterTraceListener(
                memStream, new TextFormatter("{message}\n")));
            return new LogWriter(config);
        }

        void CreateTopicAndSubscription()
        {
            if (_createdTopicAndSubscription)
                return;
            _createdTopicAndSubscription = true;
            try
            {
                _pubsub.Projects.Topics.Create(new Topic() { Name = _topicPath }, _topicPath)
                    .Execute();
            }
            catch (Google.GoogleApiException e)
            {
                // A 409 is ok.  It means the topic already exists.
                if (e.Error.Code != 409)
                    throw;
            }
            try
            {
                _pubsub.Projects.Subscriptions.Create(new Subscription()
                {
                    Name = _subscriptionPath,
                    Topic = _topicPath
                }, _subscriptionPath).Execute();
            }
            catch (Google.GoogleApiException e)
            {
                // A 409 is ok.  It means the subscription already exists.
                if (e.Error.Code != 409)
                    throw;
            }
        }

        void ClearSubscription()
        {
            while (true)
            {
                var pullResponse = _pubsub.Projects.Subscriptions.Pull(
                    new PullRequest()
                    {
                        MaxMessages = 100,
                        ReturnImmediately = true
                    }, _subscriptionPath).Execute();
                if (pullResponse.ReceivedMessages == null
                    || pullResponse.ReceivedMessages.Count == 0)
                    break;
                _pubsub.Projects.Subscriptions.Acknowledge(new AcknowledgeRequest()
                {
                    AckIds = (from msg in pullResponse.ReceivedMessages select msg.AckId).ToList()
                }, _subscriptionPath).Execute();
            }
        }

        string EncodeData(string message)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        }

        string GetLogText()
        {
            _memStream.Seek(0, SeekOrigin.Begin);
            var buffer = new byte[_memStream.Length];
            var bytesRead = _memStream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer);
        }

        static public long FutureUnixTime(long secondsFromNow)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var elapsed = DateTime.UtcNow - epoch;
            return (long)elapsed.TotalSeconds + secondsFromNow;
        }

        [Fact]
        void TestMissingAttributes()
        {
            _pubsub.Projects.Topics.Publish(new PublishRequest()
            {
                Messages = new PubsubMessage[] { new PubsubMessage()
                {
                    Data = EncodeData("hello")
                } }
            }, _topicPath).Execute();
            _shouter.ShoutOrThrow(new System.Threading.CancellationTokenSource().Token);
            Assert.True(GetLogText().Contains("Bad shout request message attributes"));
        }

        [Fact]
        void TestExpired()
        {
            _httpMessageHandler.Handler = request =>
            {
                Assert.Equal("https://localhost/", request.RequestUri.OriginalString);
                string content = request.Content.ReadAsStringAsync().Result;
                var query = System.Web.HttpUtility.ParseQueryString(content);
                Assert.Equal("AladdinsCastle", query["token"]);
                return new HttpResponseMessage();
            };
            _pubsub.Projects.Topics.Publish(new PublishRequest()
            {
                Messages = new PubsubMessage[] { new PubsubMessage()
                {
                    Data = EncodeData("hello"),
                    Attributes = new Dictionary<string, string>
                    {
                        {"postStatusUrl", "https://localhost/" },
                        {"postStatusToken", "AladdinsCastle" },
                        {"deadline", "0" },
                    }                    
                } }
            }, _topicPath).Execute();
            _shouter.ShoutOrThrow(new System.Threading.CancellationTokenSource().Token);
            string logText = GetLogText();
            Assert.Contains("Request timed out.", logText);
        }

        [Fact]
        void TestHello()
        {
            bool sawHello = false;
            _httpMessageHandler.Handler = request =>
            {
                Assert.Equal("https://localhost/", request.RequestUri.OriginalString);
                string content = request.Content.ReadAsStringAsync().Result;
                var query = System.Web.HttpUtility.ParseQueryString(content);
                Assert.Equal("AladdinsCastle", query["token"]);
                if ("success" == query["status"])
                    sawHello = "HELLO" == query["result"];
                return new HttpResponseMessage();
            };
            _pubsub.Projects.Topics.Publish(new PublishRequest()
            {
                Messages = new PubsubMessage[] { new PubsubMessage()
                {
                    Data = EncodeData("hello"),
                    Attributes = new Dictionary<string, string>
                    {
                        {"postStatusUrl", "https://localhost/" },
                        {"postStatusToken", "AladdinsCastle" },
                        {"deadline",  FutureUnixTime(30).ToString() },
                    }
                } }
            }, _topicPath).Execute();
            _shouter.ShoutOrThrow(new System.Threading.CancellationTokenSource().Token);
            Assert.True(sawHello);
            string logText = GetLogText();
            Assert.False(logText.Contains("Request timed out."), logText);
        }

        [Fact]
        void TestChickenError()
        {
            bool succeeded = false;
            _httpMessageHandler.Handler = request =>
            {
                Assert.Equal("https://localhost/", request.RequestUri.OriginalString);
                string content = request.Content.ReadAsStringAsync().Result;
                var query = System.Web.HttpUtility.ParseQueryString(content);
                Assert.Equal("AladdinsCastle", query["token"]);
                if ("success" == query["status"])
                    succeeded = true;
                return new HttpResponseMessage();
            };
            _pubsub.Projects.Topics.Publish(new PublishRequest()
            {
                Messages = new PubsubMessage[] { new PubsubMessage()
                {
                    Data = EncodeData("chickens"),
                    Attributes = new Dictionary<string, string>
                    {
                        {"postStatusUrl", "https://localhost/" },
                        {"postStatusToken", "AladdinsCastle" },
                        {"deadline",  FutureUnixTime(30).ToString() },
                    }
                } }
            }, _topicPath).Execute();
            _shouter.ShoutOrThrow(new System.Threading.CancellationTokenSource().Token);
            string logText = GetLogText();
            Assert.False(succeeded);
            Assert.Contains("Fatal", logText);
            Assert.Contains("Oh no!", logText);
        }
    }
}
