using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Networking.Transport.Utilities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Unity.Networking.Transport
{
    internal struct IPCManager
    {
        public static IPCManager Instance = new IPCManager();

        [StructLayout(LayoutKind.Explicit)]
        internal unsafe struct IPCData
        {
            [FieldOffset(0)] public int from;
            [FieldOffset(4)] public int length;
            [FieldOffset(8)] public fixed byte data[NetworkParameterConstants.MTU];
        }

        private NativeMultiQueue<IPCData> m_IPCQueue;
        private NativeHashMap<ushort, int> m_IPCChannels;

        internal static JobHandle ManagerAccessHandle;

        public bool IsCreated => m_IPCQueue.IsCreated;

        private int m_RefCount;

        public void AddRef()
        {
            if (m_RefCount == 0)
            {
                m_IPCQueue = new NativeMultiQueue<IPCData>(128);
                m_IPCChannels = new NativeHashMap<ushort, int>(64, Allocator.Persistent);
            }
            ++m_RefCount;
        }

        public void Release()
        {
            --m_RefCount;
            if (m_RefCount == 0)
            {
                ManagerAccessHandle.Complete();
                m_IPCQueue.Dispose();
                m_IPCChannels.Dispose();
            }
        }

        internal unsafe void Update(NetworkInterfaceEndPoint local, NativeQueue<QueuedSendMessage> queue)
        {
            QueuedSendMessage val;
            while (queue.TryDequeue(out val))
            {
                var ipcData = new IPCData();
                UnsafeUtility.MemCpy(ipcData.data, val.Data, val.DataLength);
                ipcData.length = val.DataLength;
                ipcData.from = *(int*)local.data;
                m_IPCQueue.Enqueue(*(int*)val.Dest.data, ipcData);
            }
        }

        public unsafe NetworkInterfaceEndPoint CreateEndPoint(ushort port)
        {
            ManagerAccessHandle.Complete();
            int id = 0;
            if (port == 0)
            {
                var rnd = new Random();
                while (id == 0)
                {
                    port = (ushort)rnd.Next(1, 0xffff);
                    int tmp;
                    if (!m_IPCChannels.TryGetValue(port, out tmp))
                    {
                        id = m_IPCChannels.Length + 1;
                        m_IPCChannels.TryAdd(port, id);
                    }
                }

            }
            else
            {
                if (!m_IPCChannels.TryGetValue(port, out id))
                {
                    id = m_IPCChannels.Length + 1;
                    m_IPCChannels.TryAdd(port, id);
                }
            }

            var endpoint = default(NetworkInterfaceEndPoint);
            endpoint.dataLength = 4;
            *(int*) endpoint.data = id;

            return endpoint;
        }
        public unsafe bool GetEndPointPort(NetworkInterfaceEndPoint ep, out ushort port)
        {
            ManagerAccessHandle.Complete();
            int id = *(int*) ep.data;
            var values = m_IPCChannels.GetValueArray(Allocator.Temp);
            var keys = m_IPCChannels.GetKeyArray(Allocator.Temp);
            port = 0;
            for (var i = 0; i < m_IPCChannels.Length; ++i)
            {
                if (values[i] == id)
                {
                    port = keys[i];
                    return true;
                }
            }

            return false;
        }

        public unsafe int PeekNext(NetworkInterfaceEndPoint local, void* slice, out int length, out NetworkInterfaceEndPoint from)
        {
            ManagerAccessHandle.Complete();
            IPCData data;
            from = default(NetworkInterfaceEndPoint);
            length = 0;

            if (m_IPCQueue.Peek(*(int*)local.data, out data))
            {
                UnsafeUtility.MemCpy(slice, data.data, data.length);

                length = data.length;
            }

            GetEndPointByHandle(data.from, out from);

            return length;
        }

        public unsafe int ReceiveMessageEx(NetworkInterfaceEndPoint local, network_iovec* iov, int iov_len, ref NetworkInterfaceEndPoint remote)
        {
            IPCData data;
            if (!m_IPCQueue.Peek(*(int*)local.data, out data))
                return 0;
            GetEndPointByHandle(data.from, out remote);

            int totalLength = 0;
            for (int i = 0; i < iov_len; i++)
            {
                var curLength = Math.Min(iov[i].len, data.length - totalLength);
                UnsafeUtility.MemCpy(iov[i].buf, data.data + totalLength, curLength);
                totalLength += curLength;
                iov[i].len = curLength;
            }

            if (totalLength < data.length)
                return -1;
            m_IPCQueue.Dequeue(*(int*)local.data, out data);

            return totalLength;
        }

        private unsafe void GetEndPointByHandle(int handle, out NetworkInterfaceEndPoint endpoint)
        {
            var temp = default(NetworkInterfaceEndPoint);
            temp.dataLength = 4;
            *(int*)temp.data = handle;

            endpoint = temp;
        }
    }
}