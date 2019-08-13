﻿using NeoExpress.Abstractions;
using Neo.Express.Backend2.Persistence;
using Neo.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.Express.Backend2
{
    public partial class Neo2Backend : INeoBackend
    {
        //public static string ROOT_PATH => Path.Combine(
        //    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        //    "NEO-Express", "backend2", "blockchain-nodes");

        public ExpressChain CreateBlockchain(int count, ushort port)
        {
            if ((uint)port + (count * 3) >= ushort.MaxValue)
            {
                // TODO: better error message
                throw new Exception("Invalid port");
            }

            var wallets = new List<(DevWallet wallet, Wallets.WalletAccount account)>(count);

            try
            {
                for (int i = 1; i <= count; i++)
                {
                    var wallet = new DevWallet($"node{i}");
                    var account = wallet.CreateAccount();
                    account.IsDefault = true;
                    wallets.Add((wallet, account));
                }

                var keys = wallets.Select(t => t.account.GetKey().PublicKey).ToArray();

                var contract = Neo.SmartContract.Contract.CreateMultiSigContract((keys.Length * 2 / 3) + 1, keys);

                foreach (var (wallet, account) in wallets)
                {
                    var multiSigContractAccount = wallet.CreateAccount(contract, account.GetKey());
                    multiSigContractAccount.Label = "MultiSigContract";
                }

                return new ExpressChain()
                {
                    Magic = ExpressChain.GenerateMagicValue(),
                    ConsensusNodes = wallets.Select(t => new ExpressConsensusNode()
                    {
                        TcpPort = port++,
                        WebSocketPort = port++,
                        RpcPort = port++,
                        Wallet = t.wallet.ToExpressWallet()
                    }).ToList()
                };
            }
            finally
            {
                foreach (var (wallet, _) in wallets)
                {
                    wallet.Dispose();
                }
            }
        }

        public ExpressWallet CreateWallet(string name)
        {
            using (var wallet = new DevWallet(name))
            {
                var account = wallet.CreateAccount();
                account.IsDefault = true;
                return wallet.ToExpressWallet();
            }
        }

        private const string ADDRESS_FILENAME = "ADDRESS.neo-express";

        private static string GetAddressFilePath(string directory) => 
            Path.Combine(directory, ADDRESS_FILENAME);

        public void CreateCheckpoint(ExpressChain chain, string chainDirectory, string checkPointDirectory)
        {
            using (var db = new RocksDbStore(chainDirectory))
            {
                db.CheckPoint(checkPointDirectory);
            }

            using (var stream = File.OpenWrite(GetAddressFilePath(checkPointDirectory)))
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine(chain.Magic);
                writer.WriteLine(chain.ConsensusNodes[0].Wallet.DefaultAccount.ScriptHash);
            }
        }

        public void RestoreCheckpoint(ExpressChain chain, string chainDirectory, string checkPointDirectory)
        {
            var node = chain.ConsensusNodes[0];
            ValidateCheckpoint(checkPointDirectory, chain.Magic, node.Wallet.DefaultAccount);

            var addressFile = GetAddressFilePath(checkPointDirectory);
            if (!File.Exists(addressFile))
            {
                File.Delete(addressFile);
            }

            Directory.Move(checkPointDirectory, chainDirectory);
        }

        private static void ValidateCheckpoint(string checkPointDirectory, long magic, ExpressWalletAccount account)
        {
            var addressFile = GetAddressFilePath(checkPointDirectory);
            if (!File.Exists(addressFile))
            {
                throw new Exception("Invalid Checkpoint");
            }

            long checkPointMagic;
            string scriptHash;
            using (var stream = File.OpenRead(addressFile))
            using (var reader = new StreamReader(stream))
            {
                checkPointMagic = long.Parse(reader.ReadLine());
                scriptHash = reader.ReadLine();
            }

            if (magic != checkPointMagic || scriptHash != account.ScriptHash)
            {
                throw new Exception("Invalid Checkpoint");
            }
        }

        private static CancellationTokenSource Run(Store store, ExpressConsensusNode node, Action<string> writeConsole)
        {
            var cts = new CancellationTokenSource();

            Task.Factory.StartNew(() =>
            {
                try
                {
                    var wallet = DevWallet.FromExpressWallet(node.Wallet);
                    using (var system = new NeoSystem(store))
                    {
                        var logPlugin = new LogPlugin(writeConsole);
                        var rpcPlugin = new ExpressNodeRpcPlugin();

                        system.StartNode(node.TcpPort, node.WebSocketPort);
                        system.StartConsensus(wallet);
                        system.StartRpc(IPAddress.Any, node.RpcPort, wallet);

                        cts.Token.WaitHandle.WaitOne();
                    }
                }
                catch (Exception ex)
                {
                    writeConsole(ex.ToString());
                    cts.Cancel();
                }
                finally
                {
                    if (store is IDisposable disp)
                    {
                        disp.Dispose();
                    }
                }
            });

            return cts;
        }

        public CancellationTokenSource RunBlockchain(string directory, ExpressChain chain, int index, uint secondsPerBlock, Action<string> writeConsole)
        {
            chain.InitializeProtocolSettings(secondsPerBlock);

            var node = chain.ConsensusNodes[index];

            return Run(new RocksDbStore(directory), node, writeConsole);
        }

        public CancellationTokenSource RunCheckpoint(string directory, ExpressChain chain, uint secondsPerBlock, Action<string> writeConsole)
        {
            chain.InitializeProtocolSettings(secondsPerBlock);

            var node = chain.ConsensusNodes[0];
            ValidateCheckpoint(directory, chain.Magic, node.Wallet.DefaultAccount);

            return Run(new CheckpointStore(directory), node, writeConsole);
        }
    }
}
