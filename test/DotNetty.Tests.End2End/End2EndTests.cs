﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Tests.End2End
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading.Tasks;
    using DotNetty.Buffers;
    using DotNetty.Codecs.Mqtt;
    using DotNetty.Codecs.Mqtt.Packets;
    using DotNetty.Common.Concurrency;
    using DotNetty.Handlers.Tls;
    using DotNetty.Transport.Bootstrapping;
    using DotNetty.Transport.Channels;
    using DotNetty.Transport.Channels.Sockets;
    using Xunit;
    using Xunit.Abstractions;

    public class End2EndTests
    {
        readonly ITestOutputHelper output;
        static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
        const int Port = 8009;

        public End2EndTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        const string ClientId = "scenarioClient1";
        const string SubscribeTopicFilter1 = "test/+";
        const string SubscribeTopicFilter2 = "test2/#";
        const string PublishC2STopic = "loopback/qosZero";
        const string PublishC2SQos0Payload = "C->S, QoS 0 test.";
        const string PublishC2SQos1Topic = "loopback2/qos/One";
        const string PublishC2SQos1Payload = "C->S, QoS 1 test. Different data length.";
        const string PublishS2CQos1Topic = "test2/scenarioClient1/special/qos/One";
        const string PublishS2CQos1Payload = "S->C, QoS 1 test. Different data length #2.";

        [Fact]
        public async void EchoServerAndClient()
        {
            var testPromise = new TaskCompletionSource();
            var tlsCertificate = new X509Certificate2("dotnetty.com.pfx", "password");
            Func<Task> closeServerFunc = await this.StartServerAsync(true, ch =>
            {
                ch.Pipeline.AddLast(TlsHandler.Server(tlsCertificate));
                ch.Pipeline.AddLast(new EchoChannelHandler());
            }, testPromise);

            var group = new MultithreadEventLoopGroup();
            Bootstrap b = new Bootstrap()
                .Group(group)
                .Channel<TcpSocketChannel>()
                .Option(ChannelOption.TcpNodelay, true)
                .Handler(new ActionChannelInitializer<ISocketChannel>(ch =>
                {
                    ch.Pipeline.AddLast(TlsHandler.Client("dotnetty.com", null, (sender, certificate, chain, errors) => true));
                    ch.Pipeline.AddLast(new TestScenarioRunner(this.GetEchoClientScenario, testPromise));
                }));

            this.output.WriteLine("Configured Bootstrap: {0}", b);

            IChannel clientChannel = null;
            try
            {
                clientChannel = await b.ConnectAsync(IPAddress.Loopback, Port);

                this.output.WriteLine("Connected channel: {0}", clientChannel);

                await Task.WhenAny(testPromise.Task, Task.Delay(TimeSpan.FromMinutes(1)));
                Assert.True(testPromise.Task.IsCompleted);
            }
            finally
            {
                Task serverCloseTask = closeServerFunc();
                if (clientChannel != null)
                {
                    clientChannel.CloseAsync().Wait(TimeSpan.FromSeconds(5));
                }
                if (!Task.WaitAll(new[] { serverCloseTask, group.ShutdownGracefullyAsync() }, ShutdownTimeout))
                {
                    this.output.WriteLine("Loops didn't stop in time.");
                }
            }
        }

        [Fact]
        public async void MqttServerAndClient()
        {
            var testPromise = new TaskCompletionSource();

            var tlsCertificate = new X509Certificate2("dotnetty.com.pfx", "password");
            Func<Task> closeServerFunc = await this.StartServerAsync(true, ch =>
            {
                ch.Pipeline.AddLast(TlsHandler.Server(tlsCertificate));
                ch.Pipeline.AddLast(
                    MqttEncoder.Instance,
                    new MqttDecoder(true, 256 * 1024),
                    new TestScenarioRunner(this.GetMqttServerScenario, testPromise));
            }, testPromise);

            var group = new MultithreadEventLoopGroup();
            Bootstrap b = new Bootstrap()
                .Group(group)
                .Channel<TcpSocketChannel>()
                .Option(ChannelOption.TcpNodelay, true)
                .Handler(new ActionChannelInitializer<ISocketChannel>(ch =>
                {
                    ch.Pipeline.AddLast(TlsHandler.Client("dotnetty.com", null, (sender, certificate, chain, errors) => true));
                    ch.Pipeline.AddLast(
                        MqttEncoder.Instance,
                        new MqttDecoder(false, 256 * 1024),
                        new TestScenarioRunner(this.GetMqttClientScenario, testPromise));
                }));

            this.output.WriteLine("Configured Bootstrap: {0}", b);

            IChannel clientChannel = null;
            try
            {
                clientChannel = await b.ConnectAsync(IPAddress.Loopback, Port);

                this.output.WriteLine("Connected channel: {0}", clientChannel);

                await Task.WhenAny(testPromise.Task, Task.Delay(TimeSpan.FromMinutes(1)));
                Assert.True(testPromise.Task.IsCompleted);
            }
            finally
            {
                Task serverCloseTask = closeServerFunc();
                if (clientChannel != null)
                {
                    clientChannel.CloseAsync().Wait(TimeSpan.FromSeconds(5));
                }
                if (!Task.WaitAll(new[] { serverCloseTask, group.ShutdownGracefullyAsync() }, ShutdownTimeout))
                {
                    this.output.WriteLine("Loops didn't stop in time.");
                }
            }
        }

        IEnumerable<TestScenarioStep> GetMqttClientScenario(Func<object> currentMessageFunc)
        {
            yield return TestScenarioStep.Message(new ConnectPacket
            {
                ClientId = ClientId,
                Username = "testuser",
                Password = "notsafe",
                WillTopic = "last/word",
                WillMessage = Unpooled.WrappedBuffer(Encoding.UTF8.GetBytes("oops"))
            });

            var connAckPacket = Assert.IsType<ConnAckPacket>(currentMessageFunc());
            Assert.Equal(ConnectReturnCode.Accepted, connAckPacket.ReturnCode);

            int subscribePacketId = GetRandomPacketId();
            yield return TestScenarioStep.Message(new SubscribePacket
            {
                PacketId = subscribePacketId,
                Requests = new[]
                {
                    new SubscriptionRequest(SubscribeTopicFilter1, QualityOfService.ExactlyOnce),
                    new SubscriptionRequest(SubscribeTopicFilter2, QualityOfService.AtLeastOnce)
                }
            });

            var subAckPacket = Assert.IsType<SubAckPacket>(currentMessageFunc());
            Assert.Equal(subscribePacketId, subAckPacket.PacketId);
            Assert.Equal(2, subAckPacket.ReturnCodes.Count);
            Assert.Equal(QualityOfService.ExactlyOnce, subAckPacket.ReturnCodes[0]);
            Assert.Equal(QualityOfService.AtLeastOnce, subAckPacket.ReturnCodes[1]);

            int publishQoS1PacketId = GetRandomPacketId();
            yield return TestScenarioStep.Messages(
                new PublishPacket(QualityOfService.AtMostOnce, false, false)
                {
                    TopicName = PublishC2STopic,
                    Payload = Unpooled.WrappedBuffer(Encoding.UTF8.GetBytes(PublishC2SQos0Payload))
                },
                new PublishPacket(QualityOfService.AtLeastOnce, false, false)
                {
                    PacketId = publishQoS1PacketId,
                    TopicName = PublishC2SQos1Topic,
                    Payload = Unpooled.WrappedBuffer(Encoding.UTF8.GetBytes(PublishC2SQos1Payload))
                });
            //new PublishPacket(QualityOfService.AtLeastOnce, false, false) { TopicName = "feedback/qos/One", Payload = Unpooled.WrappedBuffer(Encoding.UTF8.GetBytes("QoS 1 test. Different data length.")) });

            var pubAckPacket = Assert.IsType<PubAckPacket>(currentMessageFunc());
            Assert.Equal(publishQoS1PacketId, pubAckPacket.PacketId);

            yield return TestScenarioStep.MoreFeedbackExpected();

            var publishPacket = Assert.IsType<PublishPacket>(currentMessageFunc());
            Assert.Equal(QualityOfService.AtLeastOnce, publishPacket.QualityOfService);
            Assert.Equal(PublishS2CQos1Topic, publishPacket.TopicName);
            Assert.Equal(PublishS2CQos1Payload, Encoding.UTF8.GetString(publishPacket.Payload.ToArray()));

            yield return TestScenarioStep.Messages(
                PubAckPacket.InResponseTo(publishPacket),
                DisconnectPacket.Instance);
        }

        IEnumerable<TestScenarioStep> GetMqttServerScenario(Func<object> currentMessageFunc)
        {
            yield return TestScenarioStep.MoreFeedbackExpected();

            var connectPacket = Assert.IsType<ConnectPacket>(currentMessageFunc());
            // todo verify

            yield return TestScenarioStep.Message(new ConnAckPacket
            {
                ReturnCode = ConnectReturnCode.Accepted,
                SessionPresent = true
            });

            var subscribePacket = Assert.IsType<SubscribePacket>(currentMessageFunc());
            // todo verify

            yield return TestScenarioStep.Message(SubAckPacket.Confirm(subscribePacket, QualityOfService.ExactlyOnce));

            var publishQos0Packet = Assert.IsType<PublishPacket>(currentMessageFunc());
            // todo verify

            yield return TestScenarioStep.MoreFeedbackExpected();

            var publishQos1Packet = Assert.IsType<PublishPacket>(currentMessageFunc());
            // todo verify

            int publishQos1PacketId = GetRandomPacketId();
            yield return TestScenarioStep.Messages(PubAckPacket.InResponseTo(publishQos1Packet),
                new PublishPacket(QualityOfService.AtLeastOnce, false, false)
                {
                    PacketId = publishQos1PacketId,
                    TopicName = PublishS2CQos1Topic,
                    Payload = Unpooled.WrappedBuffer(Encoding.UTF8.GetBytes(PublishS2CQos1Payload))
                });

            var pubAckPacket = Assert.IsType<PubAckPacket>(currentMessageFunc());
            Assert.Equal(publishQos1PacketId, pubAckPacket.PacketId);

            yield return TestScenarioStep.MoreFeedbackExpected();

            var disconnectPacket = Assert.IsType<DisconnectPacket>(currentMessageFunc());
        }

        static int GetRandomPacketId()
        {
            return Guid.NewGuid().GetHashCode() & ushort.MaxValue;
        }

        /// <summary>
        /// Starts Echo server.
        /// </summary>
        /// <returns>function to trigger closure of the server.</returns>
        async Task<Func<Task>> StartServerAsync(bool tcpNoDelay, Action<IChannel> childHandlerSetupAction, TaskCompletionSource testPromise)
        {
            var bossGroup = new MultithreadEventLoopGroup(1);
            var workerGroup = new MultithreadEventLoopGroup();
            bool started = false;
            //var tlsCertificate = new X509Certificate2("dotnetty.com.pfx", "password");
            try
            {
                ServerBootstrap b = new ServerBootstrap()
                    .Group(bossGroup, workerGroup)
                    .Channel<TcpServerSocketChannel>()
                    .Handler(new ExceptionCatchHandler(ex => testPromise.TrySetException(ex)))
                    .ChildHandler(new ActionChannelInitializer<ISocketChannel>(childHandlerSetupAction))
                    .ChildOption(ChannelOption.TcpNodelay, tcpNoDelay);

                this.output.WriteLine("Configured ServerBootstrap: {0}", b);

                IChannel serverChannel = await b.BindAsync(Port);

                this.output.WriteLine("Bound server channel: {0}", serverChannel);

                started = true;

                return async () =>
                {
                    try
                    {
                        await serverChannel.CloseAsync();
                    }
                    finally
                    {
                        Task.WaitAll(bossGroup.ShutdownGracefullyAsync(), workerGroup.ShutdownGracefullyAsync());
                    }
                };
            }
            finally
            {
                if (!started)
                {
                    Task.WaitAll(bossGroup.ShutdownGracefullyAsync(), workerGroup.ShutdownGracefullyAsync());
                }
            }
        }

        IEnumerable<TestScenarioStep> GetEchoClientScenario(Func<object> currentMessageFunc)
        {
            string[] messages = { "message 1", string.Join(",", Enumerable.Range(1, 300)) };
            foreach (string message in messages)
            {
                yield return TestScenarioStep.Message(Unpooled.WrappedBuffer(Encoding.UTF8.GetBytes(message)));

                var responseMessage = Assert.IsAssignableFrom<IByteBuffer>(currentMessageFunc());
                Assert.Equal(message, Encoding.UTF8.GetString(responseMessage.ToArray()));
            }
        }
    }
}