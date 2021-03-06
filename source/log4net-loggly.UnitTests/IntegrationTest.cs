﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Json;
using JetBrains.Annotations;
using log4net;
using log4net.Core;
using log4net.loggly;
using log4net.Util;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;
// ReSharper disable UnusedAutoPropertyAccessor.Local

namespace log4net_loggly.UnitTests
{
    [UsedImplicitly]
    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    public abstract class IntegrationTest
    {
        protected internal const string TestThreadName = "MyTestThread";

        private readonly ManualResetEvent _messageSent;
        protected ILog _log;
        protected readonly LogglyAppender _logglyAppender;
        private readonly MemoryStream _messageStream;

        protected IntegrationTest()
        {
            // setup HTTP client mock so that we can wait for sent message and inspect it
            _messageSent = new ManualResetEvent(false);

            _messageStream = new MemoryStream();
            var webRequestMock = new Mock<WebRequest>();
            webRequestMock.Setup(x => x.GetRequestStream()).Returns(_messageStream);
            webRequestMock.Setup(x => x.GetResponse())
                .Returns(() =>
                {
                    _messageSent.Set();
                    return Mock.Of<WebResponse>();
                });
           
            // use mocked web request
            LogglyClient.WebRequestFactory = (config, url) => webRequestMock.Object;

            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
            var currentFileName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
            log4net.Config.XmlConfigurator.Configure(logRepository, new FileInfo(currentFileName + ".config"));

            _log = LogManager.GetLogger(GetType());

            var appenders = logRepository.GetAppenders();
            _logglyAppender = (LogglyAppender)appenders[0];

            ThreadContext.Properties.Clear();
            LogicalThreadContext.Properties.Clear();
            GlobalContext.Properties.Clear();

            // thread name can be set just once so we need this safeguard
            if (string.IsNullOrEmpty(Thread.CurrentThread.Name))
            {
                Thread.CurrentThread.Name = TestThreadName;
            }
        }

        [Fact]
        public void LogContainsThreadName()
        {
            _log.Info("test message");

            var message = WaitForSentMessage();
            message.ThreadName.Should().Be(TestThreadName);
        }

        [Fact]
        public void LogContainsHostName()
        {
            _log.Info("test message");

            var message = WaitForSentMessage();
            message.Hostname.Should().Be(Environment.MachineName);
        }

        [Fact]
        public void LogContainsLoggerName()
        {
            _log = LogManager.GetLogger(Assembly.GetExecutingAssembly(), "MyTestLogger");
            _log.Info("test message");

            var message = WaitForSentMessage();
            message.LoggerName.Should().Be("MyTestLogger");
        }

        [Fact]
        public void LogContainsProcessName()
        {
            _log.Info("test message");

            var message = WaitForSentMessage();
            message.Process.Should().Be(Process.GetCurrentProcess().ProcessName);
        }

        [Fact]
        public void LogContainsTimestampInLocalTime()
        {
            _log.Info("test message");

            var message = WaitForSentMessage();
            var timestamp = DateTime.Parse(message.Timestamp);
            timestamp.Should().BeWithin(TimeSpan.FromSeconds(5)).Before(DateTime.Now);
        }

        [Theory]
        [MemberData(nameof(LogLevels))]
        public void LogContainsLogLevel(Level level)
        {
            _log.Logger.Log(GetType(), level, "test message", null);

            var message = WaitForSentMessage();
            message.Level.Should().Be(level.Name);
        }

        [Fact]
        public void LogContainsPassedException()
        {
            Exception thrownException;
            try
            {
                throw new InvalidOperationException("test exception");
            }
            catch (Exception e)
            {
                thrownException = e;
                _log.Error("test message", e);
            }

            var message = WaitForSentMessage();
            var exception = message.ExtraProperties.Should().HaveElement("exception", "logged exception should be in the data").Which;
            AssertException(exception, thrownException);
        }

        [Fact]
        public void LogContainsInnerException()
        {
            Exception thrownException;
            try
            {
                try
                {
                    throw new ArgumentException("inner exception");
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException("test exception", e);
                }
            }
            catch (Exception e)
            {
                thrownException = e;
                _log.Error("test message", e);
            }

            var message = WaitForSentMessage();
            var exception = message.ExtraProperties.Should().HaveElement("exception", "logged exception should be in the data").Which;
            AssertException(exception, thrownException);
            AssertException(exception["innerException"], thrownException.InnerException);
        }

        [Fact]
        public void LogContainsEventContextProperties()
        {
            var expectedJson = @"
{
""MyProperty1"": ""MyValue1"",
""MyProperty2"": {
    ""StringProperty"": ""test string"",
    ""IntProperty"": 123,
    ""Parent"": null
  }
}";
            var data = new LoggingEventData
            {
                Level = Level.Info,
                Message = "test message",
                Properties = new PropertiesDictionary()
            };
            data.Properties["MyProperty1"] = "MyValue1";
            data.Properties["MyProperty2"] = new TestItem { IntProperty = 123, StringProperty = "test string" };

            _log.Logger.Log(new LoggingEvent(data));

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void LogContainsThreadContextProperties()
        {
            ThreadContext.Properties["MyProperty1"] = "MyValue1";
            ThreadContext.Properties["MyProperty2"] = new TestItem { IntProperty = 123, StringProperty = "test string" };
            var expectedJson = @"
{
""MyProperty1"": ""MyValue1"",
""MyProperty2"": {
    ""StringProperty"": ""test string"",
    ""IntProperty"": 123,
    ""Parent"": null
  }
}";

            _log.Info("test message");

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void LogContainsSelectedLogicalThreadContextProperties()
        {
            LogicalThreadContext.Properties["lkey1"] = "MyValue1";
            LogicalThreadContext.Properties["lkey2"] = new TestItem { IntProperty = 123, StringProperty = "test string" };
            LogicalThreadContext.Properties["lkey3"] = "this won't be in the log";
            // only properties defines in app.config in <logicalThreadContextKeys> will be included
            var expectedJson = @"
{
""lkey1"": ""MyValue1"",
""lkey2"": {
    ""StringProperty"": ""test string"",
    ""IntProperty"": 123,
    ""Parent"": null
  }
}";

            _log.Info("test message");

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void LogContainsSelectedGlobalContextProperties()
        {
            GlobalContext.Properties["gkey1"] = "MyValue1";
            GlobalContext.Properties["gkey2"] = new TestItem { IntProperty = 123, StringProperty = "test string" };
            GlobalContext.Properties["gkey3"] = "this won't be in the log";
            // only properties defines in app.config in <globalContextKeys> will be included
            var expectedJson = @"
{
""gkey1"": ""MyValue1"",
""gkey2"": {
    ""StringProperty"": ""test string"",
    ""IntProperty"": 123,
    ""Parent"": null
  }
}";

            _log.Info("test message");

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void LogContainsThreadContextStacks()
        {
            using (ThreadContext.Stacks["TestStack1"].Push("TestStackValue1"))
            {
                using (ThreadContext.Stacks["TestStack2"].Push("TestStackValue2"))
                using (ThreadContext.Stacks["TestStack1"].Push("TestStackValue3"))
                {
                    _log.Info("test message");
                }
            }
            var expectedJson = @"
{
""TestStack1"": ""TestStackValue1 TestStackValue3"",
""TestStack2"": ""TestStackValue2""
}";

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void LogContainsLogicalThreadContextStacks()
        {
            using (LogicalThreadContext.Stacks["lkey1"].Push("TestStackValue1"))
            {
                using (LogicalThreadContext.Stacks["lkey2"].Push("TestStackValue2"))
                using (LogicalThreadContext.Stacks["lkey1"].Push("TestStackValue3"))
                {
                    _log.Info("test message");
                }
            }
            var expectedJson = @"
{
""lkey1"": ""TestStackValue1 TestStackValue3"",
""lkey2"": ""TestStackValue2""
}";

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void EventContextHasHighestPriority()
        {
            GlobalContext.Properties["CommonProperty"] = "GlobalContext";
            ThreadContext.Properties["CommonProperty"] = "ThreadContext";
            LogicalThreadContext.Properties["CommonProperty"] = "LogicalThreadContext";
            var data = new LoggingEventData
            {
                Level = Level.Info,
                Message = "test message",
                Properties = new PropertiesDictionary()
            };
            data.Properties["CommonProperty"] = "EventContext";
            _log.Logger.Log(new LoggingEvent(data));

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().HaveElement("CommonProperty")
                .Which.Value<string>().Should().Be("EventContext");
        }

        [Fact]
        public void LogicalThreadContextHasSecondHighestPriority()
        {
            GlobalContext.Properties["CommonProperty"] = "GlobalContext";
            ThreadContext.Properties["CommonProperty"] = "ThreadContext";
            LogicalThreadContext.Properties["CommonProperty"] = "LogicalThreadContext";
            // no event properties here

            _log.Info("test message");

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().HaveElement("CommonProperty")
                .Which.Value<string>().Should().Be("LogicalThreadContext");
        }

        [Fact]
        public void ThreadContextHaveThirdHighestPriority()
        {
            GlobalContext.Properties["CommonProperty"] = "GlobalContext";
            ThreadContext.Properties["CommonProperty"] = "ThreadContext";
            // no event or logical thread context properties here

            _log.Info("test message");

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().HaveElement("CommonProperty")
                .Which.Value<string>().Should().Be("ThreadContext");
        }

        [Fact]
        public void PropertiesFromDifferentContextsAreMerged()
        {
            GlobalContext.Properties["gkey1"] = "GlobalContext";
            ThreadContext.Properties["tkey1"] = "ThreadContext";
            LogicalThreadContext.Properties["lkey1"] = "LogicalThreadContext";
            var data = new LoggingEventData
            {
                Level = Level.Info,
                Message = "test message",
                Properties = new PropertiesDictionary()
            };
            data.Properties["ekey1"] = "EventContext";
            var expectedJson = @"
{
""gkey1"": ""GlobalContext"",
""tkey1"": ""ThreadContext"",
""lkey1"": ""LogicalThreadContext"",
""ekey1"": ""EventContext"",
}";

            _log.Logger.Log(new LoggingEvent(data));

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void SendPlainString_DoesNotHaveAnyExtraProperties()
        {
            _log.Info("test message");

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().HaveCount(0);
        }

        [Fact]
        public void SendObject_SendsItAsJson()
        {
            var expectedJson = @"
{
    ""StringProperty"": ""test string"",
    ""IntProperty"": 123,
    ""Parent"": null
}";
            var item = new TestItem { StringProperty = "test string", IntProperty = 123 };
            _log.Info(item);
            var message = WaitForSentMessage();

            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void SendAnonymousObject_SendsItAsJson()
        {
            var expectedJson = @"
{
    ""StringProperty"": ""test string"",
    ""IntProperty"": 123
}";
            _log.Info(new { StringProperty = "test string", IntProperty = 123 });
            var message = WaitForSentMessage();

            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void SendNestedObjects_SendsItAsJson()
        {
            var expectedJson = @"
{
  ""ParentStringProperty"": ""parent"",
  ""Child"": {
    ""StringProperty"": ""test string"",
    ""IntProperty"": 123
  }
}";
            var item = new TestItem { StringProperty = "test string", IntProperty = 123 };
            var parent = new TestParentItem { ParentStringProperty = "parent", Child = item };
            item.Parent = parent;
            _log.Info(parent);
            var message = WaitForSentMessage();

            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void SendJsonString_SendsItAsJson()
        {
            var expectedJson = @"
{
    ""StringProperty"": ""test string"",
    ""IntProperty"": 123
}";

            _log.Info("{\"StringProperty\": \"test string\", \"IntProperty\": 123}");

            var message = WaitForSentMessage();
            message.ExtraProperties.Should().BeEquivalentTo(JObject.Parse(expectedJson));
        }

        [Fact]
        public void LogContainsFixedValues()
        {
            ThreadContext.Properties["TestFixValue"] = new TestFixingItem();
            _log.Info("test message");

            var message = WaitForSentMessage();
            // TestFixingItem returns "volatile value" on ToString() but "fixed value" on GetFixedObject()
            message.ExtraProperties["TestFixValue"].Should().HaveValue("fixed value", "type of this value requires fixing");
        }

        protected SentMessage WaitForSentMessage()
        {
            _messageSent.WaitOne(TimeSpan.FromSeconds(10)).Should().BeTrue("Log message should have been sent already.");
            var message = Encoding.UTF8.GetString(_messageStream.ToArray());
            return new SentMessage(message);
        }

        private void AssertException(JToken exception, Exception expectedException)
        {
            exception.Value<string>("exceptionType").Should()
                .Be(expectedException.GetType().FullName, "exception type should be correct");
            exception.Value<string>("exceptionMessage").Should().Be(expectedException.Message, "exception message should be correct");
            exception.Value<string>("stacktrace").Should().Contain(expectedException.StackTrace, "exception stack trace should be correct");
        }

        public static IEnumerable<object[]> LogLevels =>
            new[]
            {
                new[] {Level.Debug},
                new[] {Level.Info},
                new[] {Level.Warn},
                new[] {Level.Error}
            };

        protected class SentMessage
        {
            public SentMessage(string json)
            {
                OriginalJson = json;
                var jsonObject = JObject.Parse(json);
                Timestamp = jsonObject["timestamp"].Value<string>();
                jsonObject.Remove("timestamp");
                Level = jsonObject["level"].Value<string>();
                jsonObject.Remove("level");
                Hostname = jsonObject["hostName"].Value<string>();
                jsonObject.Remove("hostName");
                Process = jsonObject["process"].Value<string>();
                jsonObject.Remove("process");
                ThreadName = jsonObject["threadName"].Value<string>();
                jsonObject.Remove("threadName");
                LoggerName = jsonObject["loggerName"].Value<string>();
                jsonObject.Remove("loggerName");
                Message = jsonObject["message"]?.Value<string>();
                jsonObject.Remove("message");
                // anything that is dynamic goes as whole remaining JSON object to special property
                ExtraProperties = jsonObject;
            }

            public string OriginalJson { get; }
            public string Timestamp { get; }
            public string Level { get; }
            public string Hostname { get; }
            public string Process { get; }
            public string ThreadName { get; }
            public string LoggerName { get; }
            public string Message { get; }
            public JObject ExtraProperties { get; }
        }

        private class TestItem
        {
            public string StringProperty { get; set; }
            public int IntProperty { get; set; }
            public TestParentItem Parent { get; set; }
        }

        private class TestParentItem
        {
            public string ParentStringProperty { get; set; }
            public TestItem Child { get; set; }
        }

        private class TestFixingItem : IFixingRequired
        {
            public object GetFixedObject()
            {
                return "fixed value";
            }

            public override string ToString()
            {
                return "volatile value";
            }
        }

    }
}
