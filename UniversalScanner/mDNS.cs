﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UniversalScanner
{
    public enum mDNSType : UInt16
    {
        TYPE_A = 0x0001,     // ipv4
        TYPE_PTR = 0x000C,   // domain
        TYPE_TXT = 0x0010,   // string
        TYPE_AAAA = 0x001C,  // ipv6
        TYPE_SRV = 0x0021,   // server service
        TYPE_ANY = 0x00ff    // any
    };

    public struct mDNSAnswer
    {
        public mDNSType Type;
        public mDNSAnswerData data;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct mDNSAnswerData  /* union struct */
    {
        [FieldOffset(0)] public IPAddress typeA;
        [FieldOffset(0)] public string typePTR;
        [FieldOffset(0)] public string[] typeTXT;
        [FieldOffset(0)] public IPAddress typeAAAA;
        [FieldOffset(0)] public mDNSAnswerDataSVR typeSVR;
    }

    public struct mDNSAnswerDataSVR
    {
        public UInt16 priority;
        public UInt16 weight;
        public UInt16 port;
        public string domain;
    }

    class mDNS : ScanEngine
    {
        protected new string multicastIP = "224.0.0.251";
        protected int port = 5353;

        public override int color
        {
            get
            {
                return Color.Black.ToArgb();
            }
        }
        public override string name
        {
            get
            {
                return "mDNS";
            }
        }

        public delegate void mDNSResponse_Action(string domainFilter, mDNSAnswer[] answers);

        protected Dictionary<string, mDNSResponse_Action> resolutionTable;

        private UInt16 mDNSQuestionClass = 0x0001;

        [StructLayout(LayoutKind.Explicit, Size = 12, CharSet = CharSet.Ansi)]
        public struct mDNSHeader
        {
            [FieldOffset(0)] public UInt16 transactionID;
            [FieldOffset(2)] public UInt16 flags;
            [FieldOffset(4)] public UInt16 questions;
            [FieldOffset(6)] public UInt16 answerRRs;
            [FieldOffset(8)] public UInt16 authorityRRs;
            [FieldOffset(10)] public UInt16 additionalRRs;
        }

        public mDNS()
        {
            listenMulticast(IPAddress.Parse(multicastIP), port);
            listenUdpGlobal();

            resolutionTable = new Dictionary<string, mDNSResponse_Action>();
        }

        public void registerDomain(string domainFilter, mDNSResponse_Action onResponse)
        {
            resolutionTable.Add(domainFilter, onResponse);
        }

        private byte[] buildQuery(string queryString, mDNSType queryType)
        {
            byte[] result;
            string[] subDomains;
            int querySize, i;

            IdnMapping idn = new IdnMapping();

            subDomains = idn.GetAscii(queryString).Split('.');

            // compute size of byte needed
            querySize = 0;
            foreach (var d in subDomains)
            {
                querySize += 1 + Encoding.ASCII.GetBytes(d).Length;
            }
            querySize++; // NULL string termination; 
            querySize += 4; // type + questionClass; 
            result = new byte[querySize];

            // generate query
            i = 0;
            foreach (var d in subDomains)
            {
                byte[] currentSubDomain;
                result[i] = (byte)d.Length;
                i++;

                currentSubDomain = Encoding.ASCII.GetBytes(d);
                currentSubDomain.CopyTo(result, i);
                i += currentSubDomain.Length;
            }
            result[i++] = 0x00; // NULL string termination; 

            result[i] = (byte)((((UInt16)queryType) << 8) & 0xff);
            result[i + 1] = (byte)(((UInt16)queryType) & 0xff);
            i += 2;
            result[i++] = (byte)((mDNSQuestionClass << 8) & 0xff); // mDNSQuestionClass
            result[i++] = (byte)((mDNSQuestionClass) & 0xff);

            return result;
        }

        public void scan(string queryString, mDNSType queryType)
        {
            byte[] data;
            byte[] query;
            mDNSHeader header;
            int headerSize;
            IPEndPoint endpoint;

            header = new mDNSHeader() { transactionID = 0, flags = 0, questions = ntohs(1), answerRRs = 0, authorityRRs = 0, additionalRRs = 0 };
            query = buildQuery(queryString, queryType);

            headerSize = Marshal.SizeOf(header);
            data = new byte[headerSize + query.Length];
            IntPtr ptr = Marshal.AllocHGlobal(headerSize);
            try
            {
                Marshal.StructureToPtr<mDNSHeader>(header, ptr, false);
                Marshal.Copy(ptr, data, 0, headerSize);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            query.CopyTo(data, headerSize);

            endpoint = new IPEndPoint(IPAddress.Parse(multicastIP), port);
            if (globalListener.inUse)
            {
                try
                {
                    globalListener.udp.Send(data, data.Length, endpoint);
                }
                catch
                {
                    Trace.WriteLine("Error: mDNS.scan(): Unable to send data to {0}!", endpoint.ToString());
                }
            }
        }

        public override void reciever(IPEndPoint from, byte[] data)
        {
            mDNSHeader header;
            int headerSize;
            int expectedQueries, expectedAnwers;
            int position;

            headerSize = Marshal.SizeOf(typeof(mDNSHeader));

            if (data.Length <= headerSize)
            {
                Trace.WriteLine("mDNS.reciever(): Warning: invalid packet size.");
                return;
            }

            IntPtr ptr = Marshal.AllocHGlobal(headerSize);
            try
            {
                Marshal.Copy(data, 0, ptr, headerSize);
                header = Marshal.PtrToStructure<mDNSHeader>(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            expectedQueries = ntohs(header.questions);
            expectedAnwers = ntohs(header.authorityRRs) + ntohs(header.answerRRs) + ntohs(header.additionalRRs);
            if (expectedAnwers > 0)
            {
                position = headerSize;
                readQueries(data, ref position, expectedQueries);
                readAnswers(data, ref position, expectedAnwers);
            }
        }

        private void readQueries(byte[] data, ref int position, int expectedQueries)
        {
            UInt16 queryType, questionClass;
            string name;

            while (position < data.Length && expectedQueries > 0)
            {
                name = readString(data, ref position);
                queryType = readUInt16(data, ref position);
                questionClass = readUInt16(data, ref position);
                Trace.WriteLine(String.Format("mDNS Query '{0}', type = {1}", name, queryType));

                expectedQueries--;
            }
        }

        private void readAnswers(byte[] data, ref int position, int expectedAnswers)
        {
            UInt16 answerType, flushClass, dataLen;
            UInt32 ttl;
            string name;
            mDNSAnswer[] answers;
            int answerIndex;
            string triggerName;

            answers = new mDNSAnswer[expectedAnswers];
            answerIndex = 0;
            triggerName = null;
            while (position < data.Length && answerIndex < expectedAnswers)
            {
                name = readString(data, ref position);
                answerType = readUInt16(data, ref position);
                flushClass = readUInt16(data, ref position);
                ttl = readUInt32(data, ref position);
                dataLen = readUInt16(data, ref position);

                if (position + dataLen > data.Length)
                {
                    Trace.WriteLine("Error: readAnswer(): packet parsing overflow!");
                    return;
                }

                answers[answerIndex].Type = (mDNSType)answerType;
                switch (answerType)
                {
                    case (UInt16)mDNSType.TYPE_A:
                        IPAddress ip;
                        ip = readAnswer_A(data, ref position, dataLen);
                        answers[answerIndex].data.typeA = ip;
                        Trace.WriteLine(String.Format("* mDNS answer for '{0}': IPv4 (A) = {1}", name, ip.ToString()));
                        break;
                    case (UInt16)mDNSType.TYPE_PTR:
                        string domain;
                        domain = readAnswer_PTR(data, ref position, dataLen);
                        answers[answerIndex].data.typePTR = domain;
                        Trace.WriteLine(String.Format("* mDNS answer for '{0}': Domain (PTR) = '{1}'", name, domain));
                        break;
                    case (UInt16)mDNSType.TYPE_TXT:
                        string[] str;
                        str = readAnswer_TXT(data, ref position, dataLen);
                        answers[answerIndex].data.typeTXT = str;
                        foreach (string t in str)
                        {
                            Trace.WriteLine(String.Format("* mDNS answer for '{0}': Text (TXT) = '{1}'", name, t));
                        }
                        break;
                    case (UInt16)mDNSType.TYPE_AAAA:
                        IPAddress ipv6;
                        ipv6 = readAnswer_AAAA(data, ref position, dataLen);
                        answers[answerIndex].data.typeAAAA = ipv6;
                        Trace.WriteLine(String.Format("* mDNS answer for {0}: IPv6 (AAAA) = {1}", name, ipv6.ToString()));
                        break;
                    case (UInt16)mDNSType.TYPE_SRV:
                        mDNSAnswerDataSVR srv;
                        srv = readAnswer_SRV(data, ref position, dataLen);
                        answers[answerIndex].data.typeSVR = srv;
                        Trace.WriteLine(String.Format("* mDNS answer for '{0}': Server (SRV) = '{1}:{2}'", name, srv.domain, srv.port));
                        break;
                    default:
                        Trace.WriteLine(String.Format("* mDNS answer packet type {0} not implemented, parsing of this packet aborted!", answerType));
                        break;
                }
                if (resolutionTable.ContainsKey(name) && triggerName == null)
                {
                    triggerName = name;
                }
                answerIndex++;
            }
            if (triggerName != null)
            {
                resolutionTable[triggerName].Invoke(triggerName, answers);
            }
        }

        private IPAddress readAnswer_A(byte[] data, ref int position, int dataLen)
        {
            UInt32 ipVal;

            if (dataLen != 4)
            {
                Trace.WriteLine("Error: readAnswer_A(): Invalid address size!");
                return IPAddress.Any;
            }

            ipVal = ntohl(readUInt32(data, ref position));
            return (new IPAddress(ipVal));
        }

        private IPAddress readAnswer_AAAA(byte[] data, ref int position, int dataLen)
        {
            byte[] ipAddressBytes;

            if (dataLen != 16)
            {
                Trace.WriteLine("Error: readAnswer_AAAA(): Invalid address size!");
                return IPAddress.Any;
            }

            ipAddressBytes = new byte[16];
            Array.Copy(data, position, ipAddressBytes, 0, 16);
            return (new IPAddress(ipAddressBytes));
        }

        private string readAnswer_PTR(byte[] data, ref int position, int dataLen)
        {
            return readString(data, ref position);
        }

        private string[] readAnswer_TXT(byte[] data, ref int position, int dataLen)
        {
            byte len;
            StringBuilder sb;
            List<string> result;

            result = new List<string>();

            while (dataLen > 1 && position+1 < data.Length)
            {
                len = data[position];
                position++;
                dataLen--;

                sb = new StringBuilder();
                while (len > 0 && dataLen > 0 && position < data.Length)
                {
                    sb.Append(Convert.ToChar(data[position]));
                    position++;
                    dataLen--;
                    len--;
                }
                result.Add(sb.ToString());
            }

            return result.ToArray();
        }

        private mDNSAnswerDataSVR readAnswer_SRV(byte[] data, ref int position, int dataLen)
        {
            mDNSAnswerDataSVR result;
            if (dataLen < 6)
            {
                Trace.WriteLine("Error: readAnswer_SRV(): packet data size error!");
                result = new mDNSAnswerDataSVR { priority = 0, weight = 0, port = 0, domain=null };
                return result;
            }

            result = new mDNSAnswerDataSVR();
            result.priority = readUInt16(data, ref position);
            result.weight = readUInt16(data, ref position);
            result.port = readUInt16(data, ref position);
            result.domain = readString(data, ref position);

            return result;
        }

        private UInt16 readUInt16(byte[] data, ref int position)
        {
            UInt16 result;

            result = 0;
            if (position + 2 > data.Length)
            {
                Trace.WriteLine("Error: readUInt16(): packet parsing overflow!");
                return 0;
            }

            result |= data[position];
            position++;
            result <<= 8;
            result |= data[position];
            position++;

            return result;
        }

        private UInt32 readUInt32(byte[] data, ref int position)
        {
            UInt32 result;

            result = 0;
            if (position + 4 > data.Length)
            {
                Trace.WriteLine("Error: readUInt32(): packet parsing overflow!");
                return 0;
            }

            result |= data[position];
            position++;
            result <<= 8;
            result |= data[position];
            position++;
            result <<= 8;
            result |= data[position];
            position++;
            result <<= 8;
            result |= data[position];
            position++;

            return result;
        }

        private string readString(byte[] data, ref int position)
        {
            byte len;
            StringBuilder sb;

            sb = null;
            while (position < data.Length)
            {
                len = data[position];
                position++;

                if (len == 0)
                {
                    return (sb != null ? sb.ToString() : "");
                }

                if (sb == null)
                {
                    sb = new StringBuilder();
                }
                else
                {
                    sb.Append('.');
                }

                if ((len & 0xC0) == 0xC0)
                {
                    int positionPtr;

                    positionPtr = ((len << 8) | (data[position])) & 0x03FF;
                    position++;

                    if (positionPtr > data.Length)
                    {
                        break;
                    }

                    sb.Append(readString(data, ref positionPtr));
                    return sb.ToString();
                }
                else
                {
                    while (len > 0 && position < data.Length)
                    {
                        sb.Append(Convert.ToChar(data[position]));
                        position++;
                        len--;
                    }
                }
            }
            Trace.WriteLine("Error: readString(): packet parsing overflow!");
            return (sb != null ? sb.ToString() : "");
        }

        public override void scan()
        {
            // do nothing, different packet should be sent by each plugin
            return;
        }

        public override byte[] sender(IPEndPoint dest)
        {
            throw new NotImplementedException();
        } 
    }
}
