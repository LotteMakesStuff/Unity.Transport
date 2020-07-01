using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Networking.Transport.Protocols;

namespace Unity.Networking.Transport
{
    [BurstCompile]
    public struct IPCNetworkInterface : INetworkInterface
    {
        [ReadOnly] private NativeArray<NetworkInterfaceEndPoint> m_LocalEndPoint;

        public NetworkInterfaceEndPoint LocalEndPoint => m_LocalEndPoint[0];

        public NetworkInterfaceEndPoint CreateInterfaceEndPoint(NetworkEndPoint endPoint)
        {
            if (!endPoint.IsLoopback && !endPoint.IsAny)
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException("IPC network driver can only handle loopback addresses");
#else
                return default;
#endif
            var endpoint = IPCManager.Instance.CreateEndPoint(endPoint.Port);
            return endpoint;
        }

        public NetworkEndPoint GetGenericEndPoint(NetworkInterfaceEndPoint endpoint)
        {
            if (!IPCManager.Instance.GetEndPointPort(endpoint, out var port))
                return default;
            return NetworkEndPoint.LoopbackIpv4.WithPort(port);
        }

        public void Initialize(params INetworkParameter[] param)
        {
            IPCManager.Instance.AddRef();
            m_LocalEndPoint = new NativeArray<NetworkInterfaceEndPoint>(1, Allocator.Persistent);
            m_LocalEndPoint[0] = CreateInterfaceEndPoint(NetworkEndPoint.LoopbackIpv4);
        }

        public void Dispose()
        {
            m_LocalEndPoint.Dispose();
            IPCManager.Instance.Release();
        }

        [BurstCompile]
        struct SendUpdate : IJob
        {
            public IPCManager ipcManager;
            public NativeQueue<QueuedSendMessage> ipcQueue;
            [ReadOnly] public NativeArray<NetworkInterfaceEndPoint> localEndPoint;

            public void Execute()
            {
                ipcManager.Update(localEndPoint[0], ipcQueue);
            }
        }

        [BurstCompile]
        struct ReceiveJob : IJob
        {
            public NetworkPacketReceiver receiver;
            public IPCManager ipcManager;
            public NetworkInterfaceEndPoint localEndPoint;

            public unsafe void Execute()
            {
                var header = new UdpCHeader();
                var stream = receiver.GetDataStream();
                receiver.ReceiveCount = 0;
                receiver.ReceiveErrorCode = 0;

                while (true)
                {
                    int dataStreamSize = receiver.GetDataStreamSize();
                    if (receiver.DynamicDataStreamSize())
                    {
                        while (dataStreamSize+NetworkParameterConstants.MTU-UdpCHeader.Length >= stream.Length)
                            stream.ResizeUninitialized(stream.Length*2);
                    }
                    else if (dataStreamSize >= stream.Length)
                        return;
                    var endpoint = default(NetworkInterfaceEndPoint);
                    var result = NativeReceive(ref header, (byte*)stream.GetUnsafePtr() + dataStreamSize,
                        Math.Min(NetworkParameterConstants.MTU-UdpCHeader.Length, stream.Length - dataStreamSize), ref endpoint);
                    if (result <= 0)
                    {
                        // FIXME: handle error
                        if (result < 0)
                            receiver.ReceiveErrorCode = 10040;
                        return;
                    }

                    receiver.ReceiveCount += receiver.AppendPacket(endpoint, header, result);
                }
            }

            unsafe int NativeReceive(ref UdpCHeader header, void* data, int length, ref NetworkInterfaceEndPoint address)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (length <= 0)
                    throw new ArgumentException("Can't receive into 0 bytes or less of buffer memory");
#endif
                var iov = stackalloc network_iovec[2];

                fixed (byte* ptr = header.Data)
                {
                    iov[0].buf = ptr;
                    iov[0].len = UdpCHeader.Length;

                    iov[1].buf = data;
                    iov[1].len = length;
                }

                return ipcManager.ReceiveMessageEx(localEndPoint, iov, 2, ref address);
            }
        }

        public JobHandle ScheduleReceive(NetworkPacketReceiver receiver, JobHandle dep)
        {
            var job = new ReceiveJob
                {receiver = receiver, ipcManager = IPCManager.Instance, localEndPoint = LocalEndPoint};
            dep = job.Schedule(JobHandle.CombineDependencies(dep, IPCManager.ManagerAccessHandle));
            IPCManager.ManagerAccessHandle = dep;
            return dep;
        }
        public JobHandle ScheduleSend(NativeQueue<QueuedSendMessage> sendQueue, JobHandle dep)
        {
            var sendJob = new SendUpdate {ipcManager = IPCManager.Instance, ipcQueue = sendQueue, localEndPoint = m_LocalEndPoint};
            dep = sendJob.Schedule(JobHandle.CombineDependencies(dep, IPCManager.ManagerAccessHandle));
            IPCManager.ManagerAccessHandle = dep;
            return dep;
        }

        public unsafe int Bind(NetworkInterfaceEndPoint endpoint)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (endpoint.dataLength != 4 || *(int*)endpoint.data == 0)
                throw new InvalidOperationException();
#endif
            m_LocalEndPoint[0] = endpoint;
            return 0;
        }

        static TransportFunctionPointer<NetworkSendInterface.BeginSendMessageDelegate> BeginSendMessageFunctionPointer = new TransportFunctionPointer<NetworkSendInterface.BeginSendMessageDelegate>(BeginSendMessage);
        static TransportFunctionPointer<NetworkSendInterface.EndSendMessageDelegate> EndSendMessageFunctionPointer = new TransportFunctionPointer<NetworkSendInterface.EndSendMessageDelegate>(EndSendMessage);
        static TransportFunctionPointer<NetworkSendInterface.AbortSendMessageDelegate> AbortSendMessageFunctionPointer = new TransportFunctionPointer<NetworkSendInterface.AbortSendMessageDelegate>(AbortSendMessage);
        public NetworkSendInterface CreateSendInterface()
        {
            return new NetworkSendInterface
            {
                BeginSendMessage = BeginSendMessageFunctionPointer,
                EndSendMessage = EndSendMessageFunctionPointer,
                AbortSendMessage = AbortSendMessageFunctionPointer,
            };
        }

        [BurstCompile]
        private static unsafe int BeginSendMessage(out NetworkInterfaceSendHandle handle, IntPtr userData)
        {
            handle.id = 0;
            handle.size = 0;
            handle.capacity = NetworkParameterConstants.MTU;
            handle.data = (IntPtr)UnsafeUtility.Malloc(handle.capacity, 8, Allocator.Temp);
            return 0;
        }

        [BurstCompile]
        private static unsafe int EndSendMessage(ref NetworkInterfaceSendHandle handle, ref NetworkInterfaceEndPoint address, IntPtr userData, ref NetworkSendQueueHandle sendQueueHandle)
        {
            var sendQueue = sendQueueHandle.FromHandle();
            var msg = default(QueuedSendMessage);
            msg.Dest = address;
            msg.DataLength = handle.size;
            UnsafeUtility.MemCpy(msg.Data, (void*)handle.data, handle.size);
            sendQueue.Enqueue(msg);
            return handle.size;
        }
        [BurstCompile]
        private static void AbortSendMessage(ref NetworkInterfaceSendHandle handle, IntPtr userData)
        {
        }
    }
}