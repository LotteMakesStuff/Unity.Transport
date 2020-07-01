using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Unity.Networking.Transport.Protocols;
using Unity.Networking.Transport.Utilities;
using Random = UnityEngine.Random;

namespace Unity.Networking.Transport.Tests.Utilities
{
    using System.Linq;
    public static class Random
    {
        private static System.Random random = new System.Random();

        public static string String(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }
}

namespace Unity.Networking.Transport.Tests
{
    public struct LocalDriverHelper : IDisposable
    {
        public NetworkEndPoint EndPoint { get; }
        public NetworkDriver m_LocalDriver;
        private NativeArray<byte> m_LocalData;
        public NetworkConnection Connection { get; internal set; }
        public List<NetworkConnection> ClientConnections;

        public LocalDriverHelper(NetworkEndPoint endpoint, params INetworkParameter[] networkParams)
        {
            if (networkParams.Length == 0)
                m_LocalDriver = new NetworkDriver(new IPCNetworkInterface(), new NetworkDataStreamParameter
                    {size = NetworkParameterConstants.MTU});
            else
                m_LocalDriver = new NetworkDriver(new IPCNetworkInterface(), networkParams);
            m_LocalData = new NativeArray<byte>(NetworkParameterConstants.MTU, Allocator.Persistent);

            if (endpoint.IsValid)
            {
                EndPoint = endpoint;
            }
            else
            {
                EndPoint = NetworkEndPoint.LoopbackIpv4.WithPort(1);
            }

            Connection = default(NetworkConnection);
            ClientConnections = new List<NetworkConnection>();
        }

        public void Dispose()
        {
            m_LocalDriver.Dispose();
            m_LocalData.Dispose();
        }

        public void Update()
        {
            m_LocalDriver.ScheduleUpdate().Complete();
        }

        public NetworkConnection Accept()
        {
            return m_LocalDriver.Accept();
        }

        public void Host()
        {
            m_LocalDriver.Bind(EndPoint);
            m_LocalDriver.Listen();
        }

        public void Connect(NetworkEndPoint endpoint)
        {
            Assert.True(endpoint.IsValid);
            Connection = m_LocalDriver.Connect(endpoint);
            m_LocalDriver.ScheduleUpdate().Complete();
        }

        public unsafe void Assert_GotConnectionRequest(NetworkEndPoint from, bool accept = false)
        {
            int length;
            NetworkInterfaceEndPoint remote;
            Assert.True(EndPoint.IsLoopback || EndPoint.IsAny);
            Assert.True(from.IsLoopback || from.IsAny);
            var localEndPoint = IPCManager.Instance.CreateEndPoint(EndPoint.Port);
            var fromEndPoint = IPCManager.Instance.CreateEndPoint(from.Port);
            Assert.True(
                IPCManager.Instance.PeekNext(localEndPoint, m_LocalData.GetUnsafePtr(), out length, out remote) >=
                sizeof(UdpCHeader));

            UdpCHeader header = new UdpCHeader();
            var reader = new DataStreamReader(m_LocalData.GetSubArray(0, sizeof(UdpCHeader)));
            Assert.True(reader.IsCreated);
            reader.ReadBytes(header.Data, sizeof(UdpCHeader));
            Assert.True(header.Type == (int) UdpCProtocol.ConnectionRequest);

            Assert.True(remote == fromEndPoint);

            if (accept)
            {
                m_LocalDriver.ScheduleUpdate().Complete();
                var con = m_LocalDriver.Accept();
                ClientConnections.Add(con);
                Assert.True(con != default(NetworkConnection));
            }
        }

        public unsafe void Assert_GotDisconnectionRequest(NetworkEndPoint from)
        {
            int length;
            NetworkInterfaceEndPoint remote;
            Assert.True(EndPoint.IsLoopback || EndPoint.IsAny);
            Assert.True(from.IsLoopback || from.IsAny);
            var localEndPoint = IPCManager.Instance.CreateEndPoint(EndPoint.Port);
            var fromEndPoint = IPCManager.Instance.CreateEndPoint(from.Port);
            Assert.True(
                IPCManager.Instance.PeekNext(localEndPoint, m_LocalData.GetUnsafePtr(), out length, out remote) >=
                sizeof(UdpCHeader));

            UdpCHeader header = new UdpCHeader();
            var reader = new DataStreamReader(m_LocalData.GetSubArray(0, sizeof(UdpCHeader)));
            Assert.True(reader.IsCreated);
            reader.ReadBytes(header.Data, sizeof(UdpCHeader));
            Assert.True(header.Type == (int) UdpCProtocol.Disconnect);

            Assert.True(remote == fromEndPoint);
        }

        public unsafe void Assert_GotDataRequest(NetworkEndPoint from, byte[] dataToCompare)
        {
            NetworkInterfaceEndPoint remote = default;
            network_iovec[] iovecs = new network_iovec[2];
            iovecs[0].buf = m_LocalData.GetUnsafePtr();
            iovecs[0].len = sizeof(UdpCHeader);
            iovecs[1].buf = (byte*)m_LocalData.GetUnsafePtr() + sizeof(UdpCHeader);
            iovecs[1].len = NetworkParameterConstants.MTU;
            int dataLen = 0;
            Assert.True(EndPoint.IsLoopback || EndPoint.IsAny);
            Assert.True(from.IsLoopback || from.IsAny);
            var localEndPoint = IPCManager.Instance.CreateEndPoint(EndPoint.Port);
            var fromEndPoint = IPCManager.Instance.CreateEndPoint(from.Port);
            fixed (network_iovec* iovptr = &iovecs[0])
            {
                dataLen = IPCManager.Instance.ReceiveMessageEx(localEndPoint, iovptr, 2, ref remote);
            }

            if (dataLen <= 0)
            {
                iovecs[0].len = iovecs[1].len = 0;
            }

            Assert.True(iovecs[0].len+iovecs[1].len == dataLen);
            Assert.True(iovecs[0].len == sizeof(UdpCHeader));

            UdpCHeader header = new UdpCHeader();
            var reader = new DataStreamReader(m_LocalData.GetSubArray(0, sizeof(UdpCHeader)));
            Assert.True(reader.IsCreated);
            reader.ReadBytes(header.Data, sizeof(UdpCHeader));
            Assert.True(header.Type == (int) UdpCProtocol.Data);

            Assert.True(remote == fromEndPoint);

            Assert.True(iovecs[1].len == dataToCompare.Length);

            reader = new DataStreamReader(m_LocalData.GetSubArray(iovecs[0].len, dataToCompare.Length));
            var received = new NativeArray<byte>(dataToCompare.Length, Allocator.Temp);
            reader.ReadBytes(received);

            for (int i = 0, n = dataToCompare.Length; i < n; ++i)
                Assert.True(received[i] == dataToCompare[i]);
        }

        public unsafe void Assert_PopEventForConnection(NetworkConnection connection, NetworkEvent.Type evnt)
        {
            DataStreamReader reader;
            var retval = m_LocalDriver.PopEventForConnection(connection, out reader);
            Assert.True(retval == evnt);
        }

        public unsafe void Assert_PopEvent(out NetworkConnection connection, NetworkEvent.Type evnt)
        {
            DataStreamReader reader;

            var retval = m_LocalDriver.PopEvent(out connection, out reader);
            Assert.True(retval == evnt);
        }
    }

    public class NetworkDriverUnitTests
    {
        [Test]
        public void InitializeAndDestroyDriver()
        {
            var driver = TestNetworkDriver.Create(new NetworkDataStreamParameter {size = 64});
            driver.Dispose();
        }

        [Test]
        public void BindDriverToAEndPoint()
        {
            var driver = TestNetworkDriver.Create(new NetworkDataStreamParameter {size = 64});

            driver.Bind(NetworkEndPoint.LoopbackIpv4);
            driver.Dispose();
        }

        [Test]
        public void ListenOnDriver()
        {
            var driver = TestNetworkDriver.Create(new NetworkDataStreamParameter {size = 64});

            // Make sure we Bind before we Listen.
            driver.Bind(NetworkEndPoint.LoopbackIpv4);
            driver.Listen();

            Assert.True(driver.Listening);
            driver.Dispose();
        }

        [Test]
        public void AcceptNewConnectionsOnDriver()
        {
            var driver = TestNetworkDriver.Create(new NetworkDataStreamParameter {size = 64});

            // Make sure we Bind before we Listen.
            driver.Bind(NetworkEndPoint.LoopbackIpv4);
            driver.Listen();

            Assert.True(driver.Listening);

            //NetworkConnection connection;
            while ((/*connection =*/ driver.Accept()) != default(NetworkConnection))
            {
                //Assert.True(connectionId != NetworkParameterConstants.InvalidConnectionId);
            }

            driver.Dispose();
        }

        [Test]
        public void ConnectToARemoteEndPoint()
        {
            using (var host = new LocalDriverHelper(default(NetworkEndPoint)))
            {
                host.Host();
                var driver = new NetworkDriver(new IPCNetworkInterface(), new NetworkDataStreamParameter {size = 64});

                NetworkConnection connectionId = driver.Connect(host.EndPoint);
                Assert.True(connectionId != default(NetworkConnection));
                driver.ScheduleUpdate().Complete();

                var local = driver.LocalEndPoint();
                host.Assert_GotConnectionRequest(local);

                driver.Dispose();
            }
        }

        // TODO: Add tests where connection attempts are exceeded (connect fails)
        // TODO: Test dropped connection accept messages (accept retries happen)
        // TODO: Needs a way to explicitly assert on connect attempt stats
        // In this test multiple connect requests are received on the server, from client, might be this is expected
        // because of how the IPC driver works, but this situation is handled properly at least by basic driver logic.
        [Test]
        public void ConnectAttemptWithRetriesToARemoteEndPoint()
        {
            NetworkConnection connection;
            NetworkEvent.Type eventType = 0;
            DataStreamReader reader;

            // Tiny connect timeout for this test to be quicker
            using (var client = new NetworkDriver(new IPCNetworkInterface(), new NetworkDataStreamParameter {size = 64},
                new NetworkConfigParameter {connectTimeoutMS = 15, maxConnectAttempts = 10, fixedFrameTimeMS = 10}))
            {
                var hostAddress = NetworkEndPoint.LoopbackIpv4.WithPort(1);
                client.Connect(hostAddress);

                // Wait past the connect timeout so there will be unanswered connect requests
                client.ScheduleUpdate().Complete();
                client.ScheduleUpdate().Complete();

                using (var host = new LocalDriverHelper(hostAddress))
                {
                    host.Host();

                    // Now give the next connect attempt time to happen
                    // TODO: Would be better to be able to see internal state here and explicitly wait until next connect attempt happens
                    //client.ScheduleUpdate().Complete();

                    host.Assert_GotConnectionRequest(client.LocalEndPoint(), true);

                    // Wait for the client to get the connect event back
                    for (int i = 0; i < 2; ++i)
                    {
                        client.ScheduleUpdate().Complete();
                        eventType = client.PopEvent(out connection, out reader);
                        if (eventType != NetworkEvent.Type.Empty)
                            break;
                    }

                    Assert.AreEqual(NetworkEvent.Type.Connect, eventType);
                }
            }
        }

        [Test]
        public void DisconnectFromARemoteEndPoint()
        {
            using (var host = new LocalDriverHelper(default(NetworkEndPoint)))
            {
                host.Host();
                var driver = new NetworkDriver(new IPCNetworkInterface(), new NetworkDataStreamParameter {size = 64});

                // Need to be connected in order to be able to send a disconnect packet.
                NetworkConnection connectionId = driver.Connect(host.EndPoint);
                Assert.True(connectionId != default(NetworkConnection));
                driver.ScheduleUpdate().Complete();

                var local = driver.LocalEndPoint();
                host.Assert_GotConnectionRequest(local, true);

                NetworkConnection con;
                DataStreamReader slice;
                // Pump so we get the accept message back.
                driver.ScheduleUpdate().Complete();
                Assert.AreEqual(NetworkEvent.Type.Connect, driver.PopEvent(out con, out slice));
                driver.Disconnect(connectionId);
                driver.ScheduleUpdate().Complete();

                host.Assert_GotDisconnectionRequest(local);

                driver.Dispose();
            }
        }

        [Test]
        public void DisconnectTimeoutOnServer()
        {
            using (var host = new LocalDriverHelper(default(NetworkEndPoint),
                new NetworkConfigParameter {disconnectTimeoutMS = 40, fixedFrameTimeMS = 10}))
            using (var client = new NetworkDriver(new IPCNetworkInterface(), new NetworkConfigParameter {disconnectTimeoutMS = 40, fixedFrameTimeMS = 10}))
            {
                NetworkConnection id;
                NetworkEvent.Type popEvent = NetworkEvent.Type.Empty;
                DataStreamReader reader;

                host.Host();

                client.Connect(host.EndPoint);
                client.ScheduleUpdate().Complete();
                host.Assert_GotConnectionRequest(client.LocalEndPoint(), true);

                // Host sends stuff but gets nothing back, until disconnect timeout happens
                for (int frm = 0; frm < 10; ++frm)
                {
                    var stream = host.m_LocalDriver.BeginSend(NetworkPipeline.Null, host.ClientConnections[0]);
                    for (int i = 0; i < 100; i++)
                        stream.WriteByte((byte) i);

                    host.m_LocalDriver.EndSend(stream);
                    popEvent = host.m_LocalDriver.PopEvent(out id, out reader);
                    if (popEvent != NetworkEvent.Type.Empty)
                        break;
                    host.Update();
                }

                Assert.AreEqual(NetworkEvent.Type.Disconnect, popEvent);
            }
        }

        [Test]
        public void SendDataToRemoteEndPoint()
        {
            using (var host = new LocalDriverHelper(default))
            {
                host.Host();
                var driver = new NetworkDriver(new IPCNetworkInterface(), new NetworkDataStreamParameter {size = 64});

                // Need to be connected in order to be able to send a disconnect packet.
                NetworkConnection connectionId = driver.Connect(host.EndPoint);
                Assert.True(connectionId != default(NetworkConnection));
                driver.ScheduleUpdate().Complete();
                var local = driver.LocalEndPoint();
                host.Assert_GotConnectionRequest(local, true);

                NetworkConnection con;
                DataStreamReader slice;
                // Pump so we get the accept message back.
                driver.ScheduleUpdate().Complete();
                Assert.AreEqual(NetworkEvent.Type.Connect, driver.PopEvent(out con, out slice));

                var stream = driver.BeginSend(NetworkPipeline.Null, connectionId);
                var data = Encoding.ASCII.GetBytes("data to send");
                stream.WriteBytes(new NativeArray<byte>(data, Allocator.Temp));
                driver.EndSend(stream);
                driver.ScheduleUpdate().Complete();

                host.Assert_GotDataRequest(local, data);

                driver.Dispose();
            }
        }

        [Test]
        public void HandleEventsFromSpecificEndPoint()
        {
            using (var host = new LocalDriverHelper(default))
            using (var client0 = new LocalDriverHelper(default))
            using (var client1 = new LocalDriverHelper(default))
            {
                host.Host();
                client0.Connect(host.EndPoint);
                client1.Connect(host.EndPoint);

                host.Assert_PopEventForConnection(client0.Connection, NetworkEvent.Type.Empty);
                host.Assert_PopEventForConnection(client1.Connection, NetworkEvent.Type.Empty);

                host.Update();

                var clientConnectionId0 = host.Accept();
                Assert.True(clientConnectionId0 != default(NetworkConnection));
                var clientConnectionId1 = host.Accept();
                Assert.True(clientConnectionId1 != default(NetworkConnection));

                client1.Update();
                client1.Assert_PopEventForConnection(client1.Connection, NetworkEvent.Type.Connect);

                client0.Update();
                client0.Assert_PopEventForConnection(client0.Connection, NetworkEvent.Type.Connect);
            }
        }

        [Test]
        public void HandleEventsFromAnyEndPoint()
        {
            using (var host = new LocalDriverHelper(default))
            using (var client0 = new LocalDriverHelper(default))
            using (var client1 = new LocalDriverHelper(default))
            {
                host.Host();
                client0.Connect(host.EndPoint);
                client1.Connect(host.EndPoint);

                host.Assert_PopEventForConnection(client0.Connection, NetworkEvent.Type.Empty);
                host.Assert_PopEventForConnection(client1.Connection, NetworkEvent.Type.Empty);

                host.Update();

                var clientConnectionId0 = host.Accept();
                Assert.True(clientConnectionId0 != default(NetworkConnection));
                var clientConnectionId1 = host.Accept();
                Assert.True(clientConnectionId1 != default(NetworkConnection));

                NetworkConnection id;

                client1.Update();
                client1.Assert_PopEvent(out id, NetworkEvent.Type.Connect);
                Assert.True(id == client1.Connection);

                client0.Update();
                client0.Assert_PopEvent(out id, NetworkEvent.Type.Connect);
                Assert.True(id == client0.Connection);
            }
        }

        [Test]
        public void FillInternalBitStreamBuffer()
        {
            const int k_InternalBufferSize = 1000;
            const int k_PacketCount = 21; // Exactly enough to fill the receive buffer + 1 too much
            const int k_PacketSize = 50;

            using (var host = new NetworkDriver(new IPCNetworkInterface(), new NetworkDataStreamParameter {size = k_InternalBufferSize}))
            using (var client = new NetworkDriver(new IPCNetworkInterface(), new NetworkDataStreamParameter {size = 64}))
            {
                host.Bind(NetworkEndPoint.LoopbackIpv4);

                host.Listen();

                NetworkConnection connectionId = client.Connect(host.LocalEndPoint());

                client.ScheduleUpdate().Complete();
                host.ScheduleUpdate().Complete();

                NetworkConnection poppedId;
                DataStreamReader reader;
                host.Accept();

                client.ScheduleUpdate().Complete();

                var retval = client.PopEvent(out poppedId, out reader);
                Assert.AreEqual(retval, NetworkEvent.Type.Connect);

                var dataBlob = new Dictionary<int, byte[]>();
                for (int i = 0; i < k_PacketCount; ++i)
                {
                    // Scramble each packet contents so you can't match reading the same data twice as success
                    dataBlob.Add(i, Encoding.ASCII.GetBytes(Utilities.Random.String(k_PacketSize)));
                }

                for (int i = 0; i < k_PacketCount; ++i)
                {
                    var stream = client.BeginSend(NetworkPipeline.Null, connectionId);
                    stream.WriteBytes(new NativeArray<byte>(dataBlob[i], Allocator.Temp));
                    client.EndSend(stream);
                }

                // Process the pending events
                client.ScheduleUpdate().Complete();
                host.ScheduleUpdate().Complete();

                for (int i = 0; i < k_PacketCount; ++i)
                {
                    retval = host.PopEvent(out poppedId, out reader);

                    if (i == k_PacketCount - 1)
                    {
                        Assert.AreEqual(retval, NetworkEvent.Type.Empty);
                        Assert.IsFalse(reader.IsCreated);
                        host.ScheduleUpdate().Complete();
                        retval = host.PopEvent(out poppedId, out reader);
                    }

                    Assert.AreEqual(retval, NetworkEvent.Type.Data);
                    Assert.AreEqual(k_PacketSize, reader.Length);

                    for (int j = 0; j < k_PacketSize; ++j)
                    {
                        Assert.AreEqual(dataBlob[i][j], reader.ReadByte());
                    }
                }
            }
        }

        [Test]
        public void SendAndReceiveMessage_RealNetwork()
        {
            using (var serverDriver = NetworkDriver.Create(new NetworkDataStreamParameter {size = 64}))
            using (var clientDriver = NetworkDriver.Create(new NetworkDataStreamParameter {size = 64}))
            {
                DataStreamReader stream;

                var serverEndpoint = NetworkEndPoint.Parse("127.0.0.1", (ushort)Random.Range(2000, 65000));
                serverDriver.Bind(serverEndpoint);
                serverDriver.Listen();

                var clientToServerId = clientDriver.Connect(serverEndpoint);
                clientDriver.ScheduleFlushSend(default).Complete();

                NetworkConnection serverToClientId = default(NetworkConnection);
                // Retry a few times since the network might need some time to process
                for (int i = 0; i < 10 && serverToClientId == default(NetworkConnection); ++i)
                {
                    serverDriver.ScheduleUpdate().Complete();

                    serverToClientId = serverDriver.Accept();
                }

                Assert.That(serverToClientId != default(NetworkConnection));

                clientDriver.ScheduleUpdate().Complete();

                var eventId = clientDriver.PopEventForConnection(clientToServerId, out stream);
                Assert.That(eventId == NetworkEvent.Type.Connect);


                int testInt = 100;
                float testFloat = 555.5f;
                byte[] testByteArray = Encoding.ASCII.GetBytes("Some bytes blablabla 1111111111111111111");
                var clientSendData = clientDriver.BeginSend(NetworkPipeline.Null, clientToServerId);
                clientSendData.WriteInt(testInt);
                clientSendData.WriteFloat(testFloat);
                clientSendData.WriteInt(testByteArray.Length);
                clientSendData.WriteBytes(new NativeArray<byte>(testByteArray, Allocator.Temp));
                var sentBytes = clientDriver.EndSend(clientSendData);

                Assert.AreEqual(clientSendData.Length, sentBytes);

                clientDriver.ScheduleUpdate().Complete();
                serverDriver.ScheduleUpdate().Complete();

                DataStreamReader serverReceiveStream;
                eventId = serverDriver.PopEventForConnection(serverToClientId, out serverReceiveStream);

                Assert.True(eventId == NetworkEvent.Type.Data);
                var receivedInt = serverReceiveStream.ReadInt();
                var receivedFloat = serverReceiveStream.ReadFloat();
                var byteArrayLength = serverReceiveStream.ReadInt();
                var receivedBytes = new NativeArray<byte>(byteArrayLength, Allocator.Temp);
                serverReceiveStream.ReadBytes(receivedBytes);

                Assert.True(testInt == receivedInt);
                Assert.That(Mathf.Approximately(testFloat, receivedFloat));
                Assert.AreEqual(testByteArray, receivedBytes);
            }
        }

        [Test]
        public void SendAndReceiveMessage()
        {
            DataStreamReader stream;

            var serverDriver = new NetworkDriver(new IPCNetworkInterface(), new NetworkDataStreamParameter {size = 64});
            var serverEndpoint = NetworkEndPoint.LoopbackIpv4.WithPort(1);
            serverDriver.Bind(serverEndpoint);

            serverDriver.Listen();

            var clientDriver = new NetworkDriver(new IPCNetworkInterface(), new NetworkDataStreamParameter {size = 64});
            clientDriver.Bind(NetworkEndPoint.LoopbackIpv4);

            var clientToServerId = clientDriver.Connect(serverEndpoint);
            clientDriver.ScheduleUpdate().Complete();

            serverDriver.ScheduleUpdate().Complete();

            NetworkConnection serverToClientId = serverDriver.Accept();
            Assert.That(serverToClientId != default(NetworkConnection));

            clientDriver.ScheduleUpdate().Complete();

            var eventId = clientDriver.PopEventForConnection(clientToServerId, out stream);
            Assert.That(eventId == NetworkEvent.Type.Connect);


            int testInt = 100;
            float testFloat = 555.5f;
            byte[] testByteArray = Encoding.ASCII.GetBytes("Some bytes blablabla 1111111111111111111");
            var clientSendData = clientDriver.BeginSend(NetworkPipeline.Null, clientToServerId);
            clientSendData.WriteInt(testInt);
            clientSendData.WriteFloat(testFloat);
            clientSendData.WriteInt(testByteArray.Length);
            clientSendData.WriteBytes(new NativeArray<byte>(testByteArray, Allocator.Temp));
            var sentBytes = clientDriver.EndSend(clientSendData);

            Assert.AreEqual(clientSendData.Length, sentBytes);

            clientDriver.ScheduleUpdate().Complete();
            serverDriver.ScheduleUpdate().Complete();

            DataStreamReader serverReceiveStream;
            eventId = serverDriver.PopEventForConnection(serverToClientId, out serverReceiveStream);

            Assert.True(eventId == NetworkEvent.Type.Data);
            var receivedInt = serverReceiveStream.ReadInt();
            var receivedFloat = serverReceiveStream.ReadFloat();
            var byteArrayLength = serverReceiveStream.ReadInt();
            var receivedBytes = new NativeArray<byte>(byteArrayLength, Allocator.Temp);
            serverReceiveStream.ReadBytes(receivedBytes);

            Assert.True(testInt == receivedInt);
            Assert.That(Mathf.Approximately(testFloat, receivedFloat));
            Assert.AreEqual(testByteArray, receivedBytes);

            clientDriver.Dispose();
            serverDriver.Dispose();
        }
    }
}