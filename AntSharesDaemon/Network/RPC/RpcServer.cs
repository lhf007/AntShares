﻿using AntShares.Core;
using AntShares.IO;
using AntShares.IO.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AntShares.Network.RPC
{
    internal class RpcServer : IDisposable
    {
#if TESTNET
        private const string DEFAULT_URI_PREFIX = "http://*:20332";
#else
        private const string DEFAULT_URI_PREFIX = "http://*:10332";
#endif

        private LocalNode localNode;
        private IWebHost host;

        public RpcServer(LocalNode localNode)
        {
            this.localNode = localNode;
        }

        private static JObject CreateErrorResponse(JObject id, int code, string message, JObject data = null)
        {
            JObject response = CreateResponse(id);
            response["error"] = new JObject();
            response["error"]["code"] = code;
            response["error"]["message"] = message;
            if (data != null)
                response["error"]["data"] = data;
            return response;
        }

        private static JObject CreateResponse(JObject id)
        {
            JObject response = new JObject();
            response["jsonrpc"] = "2.0";
            response["id"] = id;
            return response;
        }

        public void Dispose()
        {
            if (host != null)
            {
                host.Dispose();
                host = null;
            }
        }

        private JObject InternalCall(string method, JArray _params)
        {
            switch (method)
            {
                case "getbestblockhash":
                    return Blockchain.Default.CurrentBlockHash.ToString();
                case "getblock":
                    {
                        Block block;
                        if (_params[0] is JNumber)
                        {
                            uint index = (uint)_params[0].AsNumber();
                            block = Blockchain.Default.GetBlock(index);
                        }
                        else
                        {
                            UInt256 hash = UInt256.Parse(_params[0].AsString());
                            block = Blockchain.Default.GetBlock(hash);
                        }
                        if (block == null)
                            throw new RpcException(-100, "Unknown block");
                        bool verbose = _params.Count >= 2 && _params[1].AsBooleanOrDefault(false);
                        if (verbose)
                            return block.ToJson();
                        else
                            return block.ToArray().ToHexString();
                    }
                case "getblockcount":
                    return Blockchain.Default.Height + 1;
                case "getblockhash":
                    {
                        uint height = (uint)_params[0].AsNumber();
                        return Blockchain.Default.GetBlockHash(height).ToString();
                    }
                case "getconnectioncount":
                    return localNode.RemoteNodeCount;
                case "getrawmempool":
                    return new JArray(LocalNode.GetMemoryPool().Select(p => (JObject)p.Hash.ToString()));
                case "getrawtransaction":
                    {
                        UInt256 hash = UInt256.Parse(_params[0].AsString());
                        bool verbose = _params.Count >= 2 && _params[1].AsBooleanOrDefault(false);
                        Transaction tx = LocalNode.GetTransaction(hash);
                        if (tx == null)
                            tx = Blockchain.Default.GetTransaction(hash);
                        if (tx == null)
                            throw new RpcException(-101, "Unknown transaction");
                        if (verbose)
                            return tx.ToJson();
                        else
                            return tx.ToArray().ToHexString();
                    }
                case "gettxout":
                    {
                        UInt256 hash = UInt256.Parse(_params[0].AsString());
                        ushort index = (ushort)_params[1].AsNumber();
                        return Blockchain.Default.GetUnspent(hash, index)?.ToJson(index);
                    }
                case "sendrawtransaction":
                    {
                        Transaction tx = Transaction.DeserializeFrom(_params[0].AsString().HexToBytes());
                        return localNode.Relay(tx);
                    }
                case "submitblock":
                    {
                        Block block = _params[0].AsString().HexToBytes().AsSerializable<Block>();
                        return localNode.Relay(block);
                    }
                default:
                    throw new RpcException(-32601, "Method not found");
            }
        }

        private async Task ProcessAsync(HttpContext context)
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Access-Control-Allow-Methods"] = "POST";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            context.Response.Headers["Access-Control-Max-Age"] = "31536000";
            if (context.Request.Method != "POST") return;
            JObject request = null;
            JObject response;
            using (StreamReader reader = new StreamReader(context.Request.Body))
            {
                try
                {
                    request = JObject.Parse(reader);
                }
                catch (FormatException) { }
            }
            if (request == null)
            {
                response = CreateErrorResponse(null, -32700, "Parse error");
            }
            else if (request is JArray)
            {
                JArray array = (JArray)request;
                if (array.Count == 0)
                {
                    response = CreateErrorResponse(request["id"], -32600, "Invalid Request");
                }
                else
                {
                    response = array.Select(p => ProcessRequest(p)).Where(p => p != null).ToArray();
                }
            }
            else
            {
                response = ProcessRequest(request);
            }
            if (response == null || (response as JArray)?.Count == 0) return;
            context.Response.ContentType = "application/json-rpc";
            await context.Response.WriteAsync(response.ToString());
        }

        private JObject ProcessRequest(JObject request)
        {
            if (!request.ContainsProperty("id")) return null;
            if (!request.ContainsProperty("method") || !request.ContainsProperty("params") || !(request["params"] is JArray))
            {
                return CreateErrorResponse(request["id"], -32600, "Invalid Request");
            }
            JObject result = null;
            try
            {
                result = InternalCall(request["method"].AsString(), (JArray)request["params"]);
            }
            catch (Exception ex)
            {
#if DEBUG
                return CreateErrorResponse(request["id"], ex.HResult, ex.Message, ex.StackTrace);
#else
                return CreateErrorResponse(request["id"], ex.HResult, ex.Message);
#endif
            }
            JObject response = CreateResponse(request["id"]);
            response["result"] = result;
            return response;
        }

        public void Start(string uriPrefix = DEFAULT_URI_PREFIX)
        {
            host = new WebHostBuilder()
                .UseKestrel()
                .UseUrls(uriPrefix)
                .Configure(app => app.Run(ProcessAsync))
                .Build();
            host.Start();
        }
    }
}
