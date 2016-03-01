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

namespace ShoutLibTest
{
    public class Test
    {
        private readonly PubsubService _pubsub;
        private readonly string _topicPath;
        private readonly string _subscriptionPath;
        private readonly Shouter _shouter;
        private readonly MemoryStream _memStream;
        private bool _createdTopicAndSubscription = false;

        public Test()
        {
            _memStream = new MemoryStream();
            var init = Shouter.Initializer.CreateDefault();
            init.LogWriter = CreateLogWriter(_memStream);
            _pubsub = init.PubsubService;
            init.SubscriptionName += "-test";
            init.ProjectId = Environment.GetEnvironmentVariable("GOOGLE_PROJECT_ID");
            _topicPath = $"projects/{init.ProjectId}/topics/{init.SubscriptionName}-topic";
            _subscriptionPath = $"projects/{init.ProjectId}/subscriptions/{init.SubscriptionName}";
            CreateTopicAndSubscription();
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

        string EncodeData(string message)
        {
            byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
            return Convert.ToBase64String(json);
        }

        string GetLogText()
        {
            _memStream.Seek(0, SeekOrigin.Begin);
            var buffer = new byte[_memStream.Length];
            var bytesRead = _memStream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer);
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
            _pubsub.Projects.Topics.Publish(new PublishRequest()
            {
                Messages = new PubsubMessage[] { new PubsubMessage()
                {
                    Data = EncodeData("hello"),
                    Attributes = new Dictionary<string, string>
                    {
                        {"postStatusUrl", "https://localhost/" },
                        {"postStatusToken", "token" },
                        {"deadline", "0" },
                    }                    
                } }
            }, _topicPath).Execute();
            _shouter.ShoutOrThrow(new System.Threading.CancellationTokenSource().Token);
            string logText = GetLogText();
            Assert.True(logText.Contains("Bad shout request message attributes"), logText);
        }
    }
}
