using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Sealed simulation fixture. This type cannot bind, connect, invoke a
    // caller-supplied sender, or transmit outside its in-process queue.
    internal sealed class ViveInMemoryDatagramSocket : IUdpSocket
    {
        public IPEndPoint LocalEndpoint {get;private set;}
        public ViveInMemoryDatagramSocket Other=null;
        public readonly Queue<Tuple<byte[],IPEndPoint>> Incoming=new Queue<Tuple<byte[],IPEndPoint>>();
        public byte[] Sent=null;public IPEndPoint Destination=null;
        public bool Closed=false,FailSend=false,FailReceive=false,FailPoll=false,FailUnexpected=false,MessageTooLarge=false,WouldBlockPoll=false;
        public ViveInMemoryDatagramSocket(IPEndPoint local)
        {
            if(local==null||!IPAddress.IsLoopback(local.Address)||local.Port==0)throw new ArgumentException("Explicit fixture loopback identity required");
            LocalEndpoint=new IPEndPoint(local.Address,local.Port);
        }
        public bool Poll()
        {
            if(Closed)throw new ObjectDisposedException("ViveInMemoryDatagramSocket");
            if(FailUnexpected)throw new IOException("synthetic transport fault");
            if(FailPoll||FailReceive)throw new SocketException((int)SocketError.ConnectionReset);
            if(WouldBlockPoll)throw new SocketException((int)SocketError.WouldBlock);
            return Incoming.Count>0;
        }
        public byte[] Receive(ref IPEndPoint sender)
        {
            if(Closed)throw new ObjectDisposedException("ViveInMemoryDatagramSocket");
            if(MessageTooLarge)throw new SocketException((int)SocketError.MessageSize);
            var item=Incoming.Dequeue();sender=item.Item2;return item.Item1;
        }
        public void Send(byte[] bytes,IPEndPoint remote)
        {
            if(Closed)throw new ObjectDisposedException("ViveInMemoryDatagramSocket");
            if(FailSend)throw new SocketException((int)SocketError.ConnectionReset);
            Sent=(byte[])bytes.Clone();Destination=new IPEndPoint(remote.Address,remote.Port);
            if(Other!=null)
            {
                if(!Other.LocalEndpoint.Equals(remote)||Other.Closed)throw new InvalidOperationException("Unexpected synthetic endpoint");
                Other.Incoming.Enqueue(Tuple.Create((byte[])bytes.Clone(),LocalEndpoint));
            }
        }
        public void Dispose(){Closed=true;}
    }
}
