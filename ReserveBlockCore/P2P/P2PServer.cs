﻿using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;


namespace ReserveBlockCore.P2P
{
    public class P2PServer : Hub
    {
        #region Broadcast methods
        public override async Task OnConnectedAsync()
        {            
            var peerIP = GetIP(Context);
            if(Globals.P2PPeerDict.TryGetValue(peerIP, out var context) && context.ConnectionId != Context.ConnectionId)
                context.Abort();

            Globals.P2PPeerDict[peerIP] = Context;

            //Save Peer here
            var peers = Peers.GetAll();
            var peerExist = peers.Find(x => x.PeerIP == peerIP).FirstOrDefault();
            if (peerExist == null)
            {
                Peers nPeer = new Peers
                {
                    FailCount = 0,
                    IsIncoming = true,
                    IsOutgoing = false,
                    PeerIP = peerIP
                };

                peers.InsertSafe(nPeer);
            }
                                    
            await Clients.Caller.SendAsync("GetMessage", "IP", peerIP);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            var peerIP = GetIP(Context);
            Globals.P2PPeerDict.TryRemove(peerIP, out var test);
        }

        #endregion

        #region GetConnectedPeerCount
        public static int GetConnectedPeerCount()
        {
            return Globals.P2PPeerDict.Count;
        }

        #endregion

        #region SignalR DOS Protection
       
        public static async Task<T> SignalRQueue<T>(HubCallerContext context, int sizeCost, Func<Task<T>> func)
        {
            if (Globals.LastBlock.Height <= Globals.BlockLock)
                return await func();

            var now = TimeUtil.GetMillisecondTime();
            var ipAddress = GetIP(context);            
            if (Globals.MessageLocks.TryGetValue(ipAddress, out var Lock))
            {                               
                var prev = Interlocked.Exchange(ref Lock.LastRequestTime, now);               
                if (Lock.ConnectionCount > 20)                
                    Peers.BanPeer(ipAddress, ipAddress + ": Connection count exceeded limit.  Peer failed to wait for responses before sending new requests.", func.Method.Name);                                        
                
                if (Lock.BufferCost + sizeCost > 5000000)
                {
                    throw new HubException("Too much buffer usage.  Message was dropped.");
                }
                if (now - prev < 1000)
                    Interlocked.Increment(ref Lock.DelayLevel);
                else
                {
                    Interlocked.CompareExchange(ref Lock.DelayLevel, 1, 0);
                    Interlocked.Decrement(ref Lock.DelayLevel);
                }

                return await SignalRQueue(Lock, sizeCost, func);
            }
            else
            {
                var newLock = new MessageLock { BufferCost = sizeCost, LastRequestTime = now, DelayLevel = 0, ConnectionCount = 0 };
                if (Globals.MessageLocks.TryAdd(ipAddress, newLock))
                    return await SignalRQueue(newLock, sizeCost, func);
                else
                {
                    Lock = Globals.MessageLocks[ipAddress];                    
                    var prev = Interlocked.Exchange(ref Lock.LastRequestTime, now);
                    if (now - prev < 1000)
                        Interlocked.Increment(ref Lock.DelayLevel);
                    else
                    {
                        Interlocked.CompareExchange(ref Lock.DelayLevel, 1, 0);
                        Interlocked.Decrement(ref Lock.DelayLevel);
                    }

                    return await SignalRQueue(Lock, sizeCost, func);
                }
            }
        }

        private static async Task<T> SignalRQueue<T>(MessageLock Lock, int sizeCost, Func<Task<T>> func)
        {
            Interlocked.Increment(ref Lock.ConnectionCount);
            Interlocked.Add(ref Lock.BufferCost, sizeCost);
            await Lock.Semaphore.WaitAsync();
            try
            {
                var task = func();
                if (Lock.DelayLevel == 0)
                    return await task;

                var delayTask = Task.Delay(500 * (1 << (Lock.DelayLevel - 1)));                
                await Task.WhenAll(delayTask, task);
                return await task;
            }
            finally
            {
                if (Lock.Semaphore.CurrentCount == 0) // finally can be executed more than once
                {
                    Interlocked.Decrement(ref Lock.ConnectionCount);
                    Interlocked.Add(ref Lock.BufferCost, -sizeCost);
                    Lock.Semaphore.Release();
                }
            }
        }

        public static async Task SignalRQueue(HubCallerContext context, int sizeCost, Func<Task> func)
        {
            var commandWrap = async () =>
            {
                await func();
                return 1;
            };
            await SignalRQueue(context, sizeCost, commandWrap);
        }

        #endregion

        #region Receive Block
        public async Task ReceiveBlock(Block nextBlock)
        {
            try
            {
                await SignalRQueue(Context, (int)nextBlock.Size, async () =>
                {
                    if (Globals.BlocksDownloading == 0)
                    {
                        if (nextBlock.ChainRefId == BlockchainData.ChainRef)
                        {
                            var IP = GetIP(Context);
                            var nextHeight = Globals.LastBlock.Height + 1;
                            var currentHeight = nextBlock.Height;

                            var isNewBlock = currentHeight >= nextHeight && !BlockDownloadService.BlockDict.ContainsKey(currentHeight);

                            if (isNewBlock)
                            {
                                BlockDownloadService.BlockDict[currentHeight] = (nextBlock, IP);
                                await BlockValidatorService.ValidateBlocks();
                            }

                            if (nextHeight == currentHeight && isNewBlock)
                            {
                                string data = "";
                                data = JsonConvert.SerializeObject(nextBlock);
                                await Clients.All.SendAsync("GetMessage", "blk", data);
                            }

                            if (nextHeight < currentHeight && isNewBlock)
                                await BlockDownloadService.GetAllBlocks();
                        }
                    }
                });
            }
            catch { }
            
        }

        #endregion

        #region Ping Peers
        public async Task<string> PingPeers()
        {
            return await SignalRQueue(Context, 1024, async () => {
                var peerIP = GetIP(Context);

                var peerDB = Peers.GetAll();

                var peer = peerDB.FindOne(x => x.PeerIP == peerIP);

                if (peer == null)
                {
                    //this does a ping back on the peer to see if it can also be an outgoing node.
                    var result = await P2PClient.PingBackPeer(peerIP);

                    Peers nPeer = new Peers
                    {
                        FailCount = 0,
                        IsIncoming = true,
                        IsOutgoing = result,
                        PeerIP = peerIP
                    };

                    peerDB.InsertSafe(nPeer);
                }
                return "HelloPeer";
            });
        }

        public async Task<string> PingBackPeer()
        {
            return await SignalRQueue(Context, 128, async () => {
                return "HelloBackPeer";
            });
        }

        #endregion

        #region Send Block Height
        public async Task<long> SendBlockHeight()
        {
            return Globals.LastBlock.Height;
        }

        #endregion

        #region Send Beacon Locator Info
        public async Task<string> SendBeaconInfo()
        {
            return await SignalRQueue(Context, 128, async () => {
                var result = "";

                var beaconInfo = BeaconInfo.GetBeaconInfo();

                if (beaconInfo == null)
                    return "NA";

                result = beaconInfo.BeaconLocator;

                return result;
            });
        }

        #endregion

        #region  ReceiveDownloadRequest
        public async Task<bool> ReceiveDownloadRequest(BeaconData.BeaconDownloadData bdd)
        {
            return await SignalRQueue(Context, 1024, async () =>
            {
                bool result = false;
                var peerIP = GetIP(Context);

                try
                {
                    if (bdd != null)
                    {
                        var scState = SmartContractStateTrei.GetSmartContractState(bdd.SmartContractUID);
                        if (scState == null)
                        {
                            return result; //fail
                        }

                        var sigCheck = SignatureService.VerifySignature(scState.OwnerAddress, bdd.SmartContractUID, bdd.Signature);
                        if (sigCheck == false)
                        {
                            return result; //fail
                        }

                        var beaconDatas = BeaconData.GetBeacon();
                        var beaconData = BeaconData.GetBeaconData();
                        foreach (var fileName in bdd.Assets)
                        {
                            if (beaconData != null)
                            {
                                var bdCheck = beaconData.Where(x => x.SmartContractUID == bdd.SmartContractUID && x.AssetName == fileName && x.NextAssetOwnerAddress == scState.OwnerAddress).FirstOrDefault();
                                if (bdCheck != null)
                                {
                                    if (beaconDatas != null)
                                    {
                                        bdCheck.DownloadIPAddress = peerIP;
                                        beaconDatas.UpdateSafe(bdCheck);
                                    }
                                    else
                                    {
                                        return result;//fail
                                    }
                                }
                                else
                                {
                                    return result; //fail
                                }
                            }
                            else
                            {
                                return result; //fail
                            }

                            result = true;
                        }

                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Error Creating BeaconData. Error Msg: {ex.ToString()}", "P2PServer.ReceiveUploadRequest()");
                }

                return result;
            });
        }

        #endregion

        #region ReceiveUploadRequest
        public async Task<bool> ReceiveUploadRequest(BeaconData.BeaconSendData bsd)
        {
            return await SignalRQueue(Context, 1024, async () =>
            {
                bool result = false;
                var peerIP = GetIP(Context);
                try
                {
                    if (bsd != null)
                    {
                        var scState = SmartContractStateTrei.GetSmartContractState(bsd.SmartContractUID);
                        if (scState == null)
                        {
                            return result;
                        }

                        var sigCheck = SignatureService.VerifySignature(scState.OwnerAddress, bsd.SmartContractUID, bsd.Signature);
                        if (sigCheck == false)
                        {
                            return result;
                        }

                        var beaconData = BeaconData.GetBeaconData();
                        foreach (var fileName in bsd.Assets)
                        {
                            if (beaconData == null)
                            {
                                var bd = new BeaconData
                                {
                                    AssetExpireDate = 0,
                                    AssetReceiveDate = 0,
                                    AssetName = fileName,
                                    IPAdress = peerIP,
                                    NextAssetOwnerAddress = bsd.NextAssetOwnerAddress,
                                    SmartContractUID = bsd.SmartContractUID
                                };

                                BeaconData.SaveBeaconData(bd);
                            }
                            else
                            {
                                var bdCheck = beaconData.Where(x => x.SmartContractUID == bsd.SmartContractUID && x.AssetName == fileName).FirstOrDefault();
                                if (bdCheck == null)
                                {
                                    var bd = new BeaconData
                                    {
                                        AssetExpireDate = 0,
                                        AssetReceiveDate = 0,
                                        AssetName = fileName,
                                        IPAdress = peerIP,
                                        NextAssetOwnerAddress = bsd.NextAssetOwnerAddress,
                                        SmartContractUID = bsd.SmartContractUID
                                    };

                                    BeaconData.SaveBeaconData(bd);
                                }
                            }
                        }

                        result = true;

                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Error Receive Upload Request. Error Msg: {ex.ToString()}", "P2PServer.ReceiveUploadRequest()");
                }

                return result;
            });
        }

        #endregion

        #region Send Adjudicator
        public async Task<Adjudicators?> SendLeadAdjudicator()
        {
            return await SignalRQueue(Context, 128, async () =>
            {
                var leadAdj = Globals.LeadAdjudicator;
                if (leadAdj == null)
                {
                    leadAdj = Adjudicators.AdjudicatorData.GetLeadAdjudicator();
                }

                return leadAdj;
            });
        }

        #endregion

        #region Send Block
        //Send Block to client from p2p server
        public async Task<Block?> SendBlock(long currentBlock)
        {
            try
            {
                //return await SignalRQueue(Context, 1179648, async () =>
                //{
                //    var peerIP = GetIP(Context);

                //    var message = "";
                //    var nextBlockHeight = currentBlock + 1;
                //    var nextBlock = BlockchainData.GetBlockByHeight(nextBlockHeight);

                //    if (nextBlock != null)
                //    {
                //        return nextBlock;
                //    }
                //    else
                //    {
                //        return null;
                //    }
                //});
                var peerIP = GetIP(Context);

                var message = "";
                var nextBlockHeight = currentBlock + 1;
                var nextBlock = BlockchainData.GetBlockByHeight(nextBlockHeight);

                if (nextBlock != null)
                {
                    return nextBlock;
                }
                else
                {
                    return null;
                }
            }
            catch { }

            return null;
            
        }

        #endregion

        #region Send to Mempool
        public async Task<string> SendTxToMempool(Transaction txReceived)
        {
            try
            {
                return await SignalRQueue(Context, (txReceived.Data?.Length ?? 0) + 1024, async () =>
                {
                    var result = "";

                    var data = JsonConvert.SerializeObject(txReceived);

                    var mempool = TransactionData.GetPool();
                    if (mempool.Count() != 0)
                    {
                        var txFound = mempool.FindOne(x => x.Hash == txReceived.Hash);
                        if (txFound == null)
                        {
                            var isTxStale = await TransactionData.IsTxTimestampStale(txReceived);
                            if (!isTxStale)
                            {
                                var txResult = await TransactionValidatorService.VerifyTX(txReceived); //sends tx to connected peers
                                if (txResult == false)
                                {
                                    try
                                    {
                                        mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                    }
                                    catch (Exception ex)
                                    {
                                        //delete failed
                                    }
                                    return "TFVP";
                                }
                                var dblspndChk = await TransactionData.DoubleSpendReplayCheck(txReceived);
                                var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(txReceived);
                                var rating = await TransactionRatingService.GetTransactionRating(txReceived);
                                txReceived.TransactionRating = rating;

                                if (txResult == true && dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                                {
                                    mempool.InsertSafe(txReceived);
                                    await P2PClient.SendTXToAdjudicator(txReceived);
                                    if (Globals.Adjudicate)
                                    {
                                        //send message to peers
                                    }
                                    return "ATMP";//added to mempool
                                }
                                else
                                {
                                    try
                                    {
                                        mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                    }
                                    catch (Exception ex)
                                    {
                                        //delete failed
                                    }
                                    return "TFVP"; //transaction failed verification process
                                }
                            }


                        }
                        else
                        {
                            var isTxStale = await TransactionData.IsTxTimestampStale(txReceived);
                            if (!isTxStale)
                            {
                                var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(txReceived);
                                if (isCraftedIntoBlock)
                                {
                                    try
                                    {
                                        mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                    }
                                    catch (Exception ex)
                                    {
                                        //delete failed
                                    }
                                }

                                return "AIMP"; //already in mempool
                            }
                            else
                            {
                                try
                                {
                                    mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                }
                                catch (Exception ex)
                                {
                                    //delete failed
                                }
                            }

                        }
                    }
                    else
                    {
                        var isTxStale = await TransactionData.IsTxTimestampStale(txReceived);
                        if (!isTxStale)
                        {
                            var txResult = await TransactionValidatorService.VerifyTX(txReceived);
                            if (!txResult)
                            {
                                try
                                {
                                    mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                }
                                catch { }

                                return "TFVP";
                            }
                            var dblspndChk = await TransactionData.DoubleSpendReplayCheck(txReceived);
                            var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(txReceived);
                            var rating = await TransactionRatingService.GetTransactionRating(txReceived);
                            txReceived.TransactionRating = rating;

                            if (txResult == true && dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                            {
                                mempool.InsertSafe(txReceived);
                                if(!Globals.Adjudicate)
                                    await P2PClient.SendTXToAdjudicator(txReceived); //sends tx to connected peers
                                return "ATMP";//added to mempool
                            }
                            else
                            {
                                try
                                {
                                    mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                }
                                catch { }

                                return "TFVP"; //transaction failed verification process
                            }
                        }

                    }

                    return "";
                });
            }
            catch { }

            return "TFVP";
        }

        #endregion

        #region Get Masternodes
        public async Task<List<Validators>?> GetMasternodes(int valCount)
        {
            return await SignalRQueue(Context, 65536, async () =>
            {
                var validatorList = Validators.Validator.GetAll();
                var validatorListCount = validatorList.Count();

                if (validatorListCount == 0)
                {
                    return null;
                }
                else
                {
                    return validatorList.FindAll().ToList();
                }
            });
        }

        #endregion

        #region Send Banned Addresses
        public async Task<List<Validators>?> GetBannedMasternodes()
        {
            return await SignalRQueue(Context, 65536, async () =>
            {
                var validatorList = Validators.Validator.GetAll();
                var validatorListCount = validatorList.Count();

                if (validatorListCount == 0)
                {
                    return null;
                }
                else
                {
                    var bannedNodes = validatorList.FindAll().Where(x => x.FailCount >= 10).ToList();
                    if (bannedNodes.Count() > 0)
                    {
                        return bannedNodes;
                    }
                }

                return null;
            });
        }
        #endregion 

        #region Check Masternode
        public async Task<bool> MasternodeOnline()
        {
            return await SignalRQueue(Context, 128, async () =>
            {
                return true;
            });
        }

        #endregion

        #region Seed node check
        public async Task<string> SeedNodeCheck()
        {
            return await SignalRQueue(Context, 1024, async () =>
            {
                //do check for validator. if yes return val otherwise return Hello.
                var validators = Validators.Validator.GetAll();
                var hasValidators = validators.FindAll().Where(x => x.NodeIP == "SELF").Count(); //revise this to use local account and IsValidating

                if (hasValidators > 0)
                    return "HelloVal";

                return "Hello";
            });
        }
        #endregion

        #region Get IP

        private static string GetIP(HubCallerContext context)
        {
            var feature = context.Features.Get<IHttpConnectionFeature>();            
            var peerIP = feature.RemoteIpAddress.MapToIPv4().ToString();

            return peerIP;
        }

        #endregion

    }
}
