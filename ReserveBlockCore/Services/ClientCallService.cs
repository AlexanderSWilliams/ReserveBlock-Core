﻿using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using ReserveBlockCore.Beacon;
using ReserveBlockCore.Data;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Utilities;
using System;
using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security;
using System.Transactions;

namespace ReserveBlockCore.Services
{
    public class ClientCallService : IHostedService, IDisposable
    {
        #region Timers and Private Variables
        private readonly IHubContext<P2PAdjServer> _hubContext;
        private readonly IHostApplicationLifetime _appLifetime;
        private int executionCount = 0;
        private Timer _timer = null!;
        private Timer _fortisPoolTimer = null!;
        private Timer _checkpointTimer = null!;
        private Timer _blockStateSyncTimer = null!;
        private Timer _encryptedPasswordTimer = null!;
        private Timer _assetTimer = null!;
        private static bool FirstRun = false;
        private static bool StateSyncLock = false;
        private static bool AssetLock = false;

        public ClientCallService(IHubContext<P2PAdjServer> hubContext, IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            _appLifetime = appLifetime;
        }

        public Task StartAsync(CancellationToken stoppingToken)
        {
            _timer = new Timer(DoWork, null, TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(2));

            _fortisPoolTimer = new Timer(DoFortisPoolWork, null, TimeSpan.FromSeconds(90),
                TimeSpan.FromSeconds(Globals.IsTestNet ? 30 : 180));

            //_blockStateSyncTimer = new Timer(DoBlockStateSyncWork, null, TimeSpan.FromSeconds(100),
            //    TimeSpan.FromHours(8));

            if (Globals.ChainCheckPoint == true)
            {
                var interval = Globals.ChainCheckPointInterval;
                
                _checkpointTimer = new Timer(DoCheckpointWork, null, TimeSpan.FromSeconds(240),
                TimeSpan.FromHours(interval));
            }

            _encryptedPasswordTimer = new Timer(DoPasswordClearWork, null, TimeSpan.FromSeconds(5),
                TimeSpan.FromMinutes(Globals.PasswordClearTime));

            _assetTimer = new Timer(DoAssetWork, null, TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5));

            return Task.CompletedTask;
        }

        #endregion

        #region Checkpoint Work
        private async void DoCheckpointWork(object? state)
        {
            var retain = Globals.ChainCheckPointRetain;
            var path = GetPathUtility.GetDatabasePath();
            var checkpointPath = Globals.ChainCheckpointLocation;
            var zipPath = checkpointPath + "checkpoint_" + DateTime.Now.Ticks.ToString();

            try
            {
                var directoryCount = Directory.GetFiles(checkpointPath).Length;
                if(directoryCount >= retain)
                {
                    FileSystemInfo fileInfo = new DirectoryInfo(checkpointPath).GetFileSystemInfos()
                        .OrderBy(fi => fi.CreationTime).First();
                    fileInfo.Delete();
                }

                ZipFile.CreateFromDirectory(path, zipPath);
                var createDate = DateTime.Now.ToString();
                LogUtility.Log($"Checkpoint successfully created at: {createDate}", "ClientCallService.DoCheckpointWork()");
            }
            catch(Exception ex)
            {
                ErrorLogUtility.LogError($"Error creating checkpoint. Error Message: {ex.ToString()}", "ClientCallService.DoCheckpointWork()");
            }
        }

        #endregion

        #region Password Clear Work

        private async void DoPasswordClearWork(object? state)
        {
            if(Globals.IsWalletEncrypted == true)
            {
                Globals.EncryptPassword.Dispose();
                Globals.EncryptPassword = new SecureString();
            }
        }
        #endregion

        #region Asset Download/Upload Work

        private async void DoAssetWork(object? state)
        {
            if (!AssetLock)
            {
                AssetLock = true;
                {
                    var currentDate = DateTime.UtcNow;
                    var aqDB = AssetQueue.GetAssetQueue();
                    if(aqDB != null)
                    {
                        var aqList = aqDB.Find(x => x.NextAttempt != null && x.NextAttempt <= currentDate && x.IsComplete != true && 
                            x.AssetTransferType == AssetQueue.TransferType.Download).ToList();

                        if(aqList.Count() > 0)
                        {
                            foreach(var aq in aqList)
                            {
                                aq.Attempts = aq.Attempts < 4 ? aq.Attempts + 1 : aq.Attempts;
                                var nextAttemptValue = AssetQueue.GetNextAttemptInterval(aq.Attempts);
                                aq.NextAttempt = DateTime.UtcNow.AddSeconds(nextAttemptValue);
                                try
                                {
                                    var result = await NFTAssetFileUtility.DownloadAssetFromBeacon(aq.SmartContractUID, aq.Locator, "NA", aq.MD5List);
                                    if(result == "Success")
                                    {
                                        NFTLogUtility.Log($"Download Request has been sent", "ClientCallService.DoAssetWork()");
                                        aq.IsComplete = true;
                                        aq.Attempts = 0;
                                        aq.NextAttempt = DateTime.UtcNow;
                                        aqDB.UpdateSafe(aq);
                                    }
                                    else
                                    {
                                        NFTLogUtility.Log($"Download Request has not been sent. Reason: {result}", "ClientCallService.DoAssetWork()");
                                        aqDB.UpdateSafe(aq);
                                    }
                                    
                                }
                                catch(Exception ex)
                                {
                                    NFTLogUtility.Log($"Error Performing Asset Download. Error: {ex.ToString()}", "ClientCallService.DoAssetWork()");
                                }
                            }
                        }

                        var aqCompleteList = aqDB.Find(x =>  x.IsComplete == true && x.IsDownloaded == false &&
                            x.AssetTransferType == AssetQueue.TransferType.Download).ToList();

                        if(aqCompleteList.Count() > 0)
                        {
                            foreach(var aq in aqCompleteList)
                            {
                                try
                                {
                                    var curDate = DateTime.UtcNow;
                                    if(aq.NextAttempt <= curDate)
                                    {
                                        await NFTAssetFileUtility.CheckForAssets(aq);
                                        aq.Attempts = aq.Attempts < 4 ? aq.Attempts + 1 : aq.Attempts;
                                        var nextAttemptValue = AssetQueue.GetNextAttemptInterval(aq.Attempts);
                                        aq.NextAttempt = DateTime.UtcNow.AddSeconds(nextAttemptValue);
                                        //attempt to get file again. call out to beacon
                                        if (aq.MediaListJson != null)
                                        {
                                            var assetList = JsonConvert.DeserializeObject<List<string>>(aq.MediaListJson);
                                            if (assetList != null)
                                            {
                                                if (assetList.Count() > 0)
                                                {
                                                    foreach (string asset in assetList)
                                                    {
                                                        var path = NFTAssetFileUtility.NFTAssetPath(asset, aq.SmartContractUID);
                                                        var fileExist = File.Exists(path);
                                                        if (!fileExist)
                                                        {
                                                            try
                                                            {
                                                                var fileCheckResult = await P2PClient.BeaconFileReadyCheck(aq.SmartContractUID, asset);
                                                                if (fileCheckResult)
                                                                {
                                                                    var beaconString = Globals.Locators.Values.FirstOrDefault().ToStringFromBase64();
                                                                    var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);

                                                                    if (beacon != null)
                                                                    {
                                                                        BeaconResponse rsp = BeaconClient.Receive(asset, beacon.IPAddress, beacon.Port, aq.SmartContractUID);
                                                                        if (rsp.Status != 1)
                                                                        {
                                                                            //failed to download
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                            catch { }
                                                        }
                                                    }
                                                   
                                                }
                                            }
                                            
                                        }
                                        
                                    }
                                    //Look to see if media exist
                                    await NFTAssetFileUtility.CheckForAssets(aq);
                                }
                                catch { }
                            }
                        }
                    }
                }

                AssetLock = false;
            }
        }
        #endregion

        #region Block State Sync Work
        private async void DoBlockStateSyncWork(object? state)
        {
            if(!StateSyncLock)
            {
                StateSyncLock = true;
                //await StateTreiSyncService.SyncAccountStateTrei();
                StateSyncLock = false;
            }
            else
            {
                //overlap has occurred.
            }
        }

        #endregion

        #region Fortis Pool Work
        private async void DoFortisPoolWork(object? state)
        {
            try
            {
                if (Globals.StopAllTimers == false)
                {
                    if (Globals.Adjudicate && !Globals.IsTestNet)
                    {
                        var currentTime = DateTime.Now.AddMinutes(-15);
                        var fortisPool = Globals.FortisPool.Values
                            .Select(x => new
                            {
                                x.Context.ConnectionId,
                                x.ConnectDate,
                                x.LastAnswerSendDate,
                                x.IpAddress,
                                x.Address,
                                x.UniqueName,
                                x.WalletVersion
                            }).ToList();

                        var fortisPoolStr = "";
                        fortisPoolStr = JsonConvert.SerializeObject(fortisPool);

                        var explorerNode = fortisPool.Where(x => x.Address == "RHNCRbgCs7KGdXk17pzRYAYPRKCkSMwasf").FirstOrDefault();

                        if (explorerNode != null)
                        {
                            try
                            {
                                await _hubContext.Clients.Client(explorerNode.ConnectionId).SendAsync("GetAdjMessage", "fortisPool", fortisPoolStr);
                            }
                            catch 
                            {
                                ErrorLogUtility.LogError("Failed to send fortis pool to RHNCRbgCs7KGdXk17pzRYAYPRKCkSMwasf", "ClientCallSerivce.DoFortisPoolWork()");
                            }
                        }
                    }
                }

                //rebroadcast TXs
                var pool = TransactionData.GetPool();
                var mempool = TransactionData.GetMempool();
                var blockHeight = Globals.LastBlock.Height;
                if(mempool != null)
                {
                    var currentTime = TimeUtil.GetTime(-60);
                    if (mempool.Count() > 0)
                    {
                        foreach(var tx in mempool)
                        {
                            var txTime = tx.Timestamp;
                            var sendTx = currentTime > txTime ? true : false;
                            if (sendTx)
                            {
                                var txResult = await TransactionValidatorService.VerifyTX(tx);
                                if (txResult == true)
                                {
                                    var dblspndChk = await TransactionData.DoubleSpendReplayCheck(tx);
                                    var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(tx);

                                    if (dblspndChk == false && isCraftedIntoBlock == false && tx.TransactionRating != TransactionRating.F)
                                    {
                                        var txOutput = "";
                                        txOutput = JsonConvert.SerializeObject(tx);
                                        await _hubContext.Clients.All.SendAsync("GetAdjMessage", "tx", txOutput);//sends messages to all in fortis pool
                                        Globals.BroadcastedTrxDict[tx.Hash] = tx;
                                    }
                                    else
                                    {
                                        try
                                        {
                                            pool.DeleteManySafe(x => x.Hash == tx.Hash);// tx has been crafted into block. Remove.
                                        }
                                        catch (Exception ex)
                                        {
                                            DbContext.Rollback();
                                            //delete failed
                                        }
                                    }
                                }
                                else
                                {
                                    try
                                    {
                                        pool.DeleteManySafe(x => x.Hash == tx.Hash);// tx has been crafted into block. Remove.
                                    }
                                    catch (Exception ex)
                                    {
                                        DbContext.Rollback();
                                        //delete failed
                                    }
                                }

                            }
                        }
                }
                }
                
            }
            catch (Exception ex)
            {
                //no node found
                Console.WriteLine("Error: ClientCallService.DoFortisPoolWork(): " + ex.ToString());
            }
        }

        #endregion

        #region Do work **Deprecated
        private async Task DoWork_Deprecated()
        {
            try
            {
                if (Globals.StopAllTimers == false)
                {
                    if (Globals.Adjudicate)
                    {
                        var fortisPool = Globals.FortisPool;

                        if (fortisPool.Count > 0)
                        {
                            if (FirstRun == false)
                            {
                                //
                                FirstRun = true;
                                Console.WriteLine("Doing the work");
                            }
                            //get last block timestamp and current timestamp if they are more than 1 mins apart start new task
                            var lastBlockSubmitUnixTime = Globals.LastAdjudicateTime;
                            var currentUnixTime = TimeUtil.GetTime();
                            var timeDiff = (currentUnixTime - lastBlockSubmitUnixTime);

                            if (timeDiff > 25)
                            {
                                if (Globals.AdjudicateLock == false)
                                {
                                    Globals.AdjudicateLock = true;

                                    //once greater commit block winner
                                    var taskAnswerList = Globals.TaskAnswerDict.Values.ToList();
                                    var taskQuestion = Globals.CurrentTaskQuestion;
                                    List<TaskAnswer>? failedTaskAnswersList = null;

                                    if (taskAnswerList.Count() > 0)
                                    {
                                        ConsoleWriterService.Output("Beginning Solve. Received Answers: " + taskAnswerList.Count().ToString());
                                        bool findWinner = true;
                                        int taskFindCount = 0;
                                        while (findWinner)
                                        {
                                            taskFindCount += 1;
                                            ConsoleWriterService.Output($"Current Task Find Count: {taskFindCount}");
                                            var taskWinner = await TaskWinnerUtility.TaskWinner(taskQuestion, failedTaskAnswersList);
                                            if (taskWinner != null)
                                            {
                                                var taskWinnerAddr = taskWinner.Address;
                                                var acctStateTreiBalance = AccountStateTrei.GetAccountBalance(taskWinnerAddr);

                                                if (acctStateTreiBalance < 1000)
                                                {
                                                    if (Globals.FortisPool.TryRemoveFromKey2(taskWinnerAddr, out var Out))
                                                        Out.Item2.Context.Abort();

                                                    ConsoleWriterService.Output("Address failed validation. Balance is too low.");
                                                    if (failedTaskAnswersList == null)
                                                    {
                                                        failedTaskAnswersList = new List<TaskAnswer>();
                                                    }
                                                    failedTaskAnswersList.Add(taskWinner);
                                                }
                                                else
                                                {
                                                    ConsoleWriterService.Output("Task Winner was Found! *DEP " + taskWinner.Address);
                                                    var nextBlock = taskWinner.Block;
                                                    if (nextBlock != null)
                                                    {
                                                        var result = await BlockValidatorService.ValidateBlock(nextBlock);
                                                        if (result == true)
                                                        {
                                                            ConsoleWriterService.Output("Task Completed and Block Found: " + nextBlock.Height.ToString());
                                                            ConsoleWriterService.Output(DateTime.Now.ToString());
                                                            string data = "";
                                                            data = JsonConvert.SerializeObject(nextBlock);

                                                            await _hubContext.Clients.All.SendAsync("GetAdjMessage", "taskResult", data);
                                                            ConsoleWriterService.Output("Sending Blocks Now - Height: " + nextBlock.Height.ToString());
                                                            //Update submit time to wait another 28 seconds to process.


                                                            //send new puzzle and wait for next challenge completion
                                                            string taskQuestionStr = "";
                                                            var nTaskQuestion = await TaskQuestionUtility.CreateTaskQuestion("rndNum");
                                                            ConsoleWriterService.Output("New Task Created.");
                                                            Globals.CurrentTaskQuestion = nTaskQuestion;
                                                            TaskQuestion nSTaskQuestion = new TaskQuestion();
                                                            nSTaskQuestion.TaskType = nTaskQuestion.TaskType;
                                                            nSTaskQuestion.BlockHeight = nTaskQuestion.BlockHeight;

                                                            taskQuestionStr = JsonConvert.SerializeObject(nSTaskQuestion);


                                                            await ProcessFortisPool_Deprecated(taskAnswerList);
                                                            ConsoleWriterService.Output("Fortis Pool Processed");

                                                            foreach (var answer in Globals.TaskAnswerDict.Values)
                                                                if (answer.Block.Height <= nextBlock.Height)
                                                                    Globals.TaskAnswerDict.TryRemove(answer.Address, out var test);

                                                            Thread.Sleep(1000);

                                                            await _hubContext.Clients.All.SendAsync("GetAdjMessage", "task", taskQuestionStr);
                                                            ConsoleWriterService.Output("Task Sent.");

                                                            findWinner = false;
                                                            taskFindCount = 0;
                                                            Globals.AdjudicateLock = false;
                                                            Globals.LastAdjudicateTime = TimeUtil.GetTime();

                                                            Globals.BroadcastedTrxDict.Clear();
                                                        }
                                                        else
                                                        {
                                                            Console.WriteLine("Block failed validation");
                                                            if (failedTaskAnswersList == null)
                                                            {
                                                                failedTaskAnswersList = new List<TaskAnswer>();
                                                            }
                                                            failedTaskAnswersList.Add(taskWinner);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        Console.WriteLine("Block was null");
                                                        if (failedTaskAnswersList == null)
                                                        {
                                                            failedTaskAnswersList = new List<TaskAnswer>();
                                                        }
                                                        failedTaskAnswersList.Add(taskWinner);
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                ConsoleWriterService.Output("Task Winner was Not Found!");
                                                if (failedTaskAnswersList != null)
                                                {
                                                    List<TaskAnswer> validTaskAnswerList = taskAnswerList.Except(failedTaskAnswersList).ToList();
                                                    if (validTaskAnswerList.Count() == 0)
                                                    {
                                                        ConsoleWriterService.Output("Error in task list");
                                                        //If this happens that means not a single task answer yielded a validatable block.
                                                        //If this happens chain must be corrupt or zero validators are online.
                                                        findWinner = false;
                                                        Globals.AdjudicateLock = false;
                                                    }
                                                }
                                                else
                                                {
                                                    ConsoleWriterService.Output("Task list failed to find winner");
                                                    failedTaskAnswersList = new List<TaskAnswer>();
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {

                                        Globals.AdjudicateLock = false;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        //dipose timer.
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
                Console.WriteLine("Client Call Service");
                Globals.AdjudicateLock = false;
            }
        }

        #endregion

        #region Do work **NEW
        public async Task DoWork_New()
        {
            try
            {
                if (Globals.StopAllTimers == false)
                {
                    if (Globals.Adjudicate)
                    {
                        var fortisPool = Globals.FortisPool.Values;

                        if (Globals.FortisPool.Count > 0)
                        {
                            if (FirstRun == false)
                            {
                                //
                                FirstRun = true;
                                Console.WriteLine("Doing the work **New**");
                            }
                            //get last block timestamp and current timestamp if they are more than 1 mins apart start new task
                            var lastBlockSubmitUnixTime = Globals.LastAdjudicateTime;
                            var currentUnixTime = TimeUtil.GetTime();
                            var timeDiff = (currentUnixTime - lastBlockSubmitUnixTime);
                            if (timeDiff > 20)
                            {
                                if (Globals.AdjudicateLock == false)
                                {
                                    Globals.AdjudicateLock = true;

                                    var taskAnswerList = Globals.TaskAnswerDict_New.Values.ToList();
                                    var taskQuestion = Globals.CurrentTaskQuestion;
                                    List<TaskNumberAnswer>? failedTaskAnswersList = null;

                                    if (taskAnswerList.Count() > 0)
                                    {
                                        await ProcessFortisPool_New(taskAnswerList);
                                        ConsoleWriterService.Output("Beginning Solve. Received Answers: " + taskAnswerList.Count().ToString());
                                        bool findWinner = true;
                                        int taskFindCount = 0;
                                        while (findWinner)
                                        {
                                            taskFindCount += 1;
                                            ConsoleWriterService.Output($"Current Task Find Count: {taskFindCount}");
                                            var taskWinner = await TaskWinnerUtility.TaskWinner_New(taskQuestion, taskAnswerList, failedTaskAnswersList);
                                            if (taskWinner != null)
                                            {
                                                var taskWinnerAddr = taskWinner.Address;
                                                var acctStateTreiBalance = AccountStateTrei.GetAccountBalance(taskWinnerAddr);

                                                if (acctStateTreiBalance < 1000)
                                                {
                                                    if (Globals.FortisPool.TryRemoveFromKey2(taskWinnerAddr, out var Out))
                                                        Out.Item2.Context.Abort();

                                                    ConsoleWriterService.Output("Address failed validation. Balance is too low.");
                                                    if (failedTaskAnswersList == null)
                                                    {
                                                        failedTaskAnswersList = new List<TaskNumberAnswer>();
                                                    }
                                                    failedTaskAnswersList.Add(taskWinner);
                                                }
                                                else
                                                {
                                                    ConsoleWriterService.Output("Task Winner was Found! " + taskWinner.Address);
                                                    List<FortisPool> winners = new List<FortisPool>();
                                                    var winner = fortisPool.Where(x => x.Address == taskWinner.Address).FirstOrDefault();
                                                    if(winner != null)
                                                    {
                                                        winners.Add(winner);
                                                    }
                                                    foreach (var chosen in Globals.TaskSelectedNumbers.Values)
                                                    {
                                                        var fortisRec = fortisPool.Where(x => x.Address == chosen.Address).FirstOrDefault();
                                                        if(fortisRec != null)
                                                        {
                                                            var alreadyIn = winners.Exists(x => x.Address == chosen.Address);
                                                            if(!alreadyIn)
                                                                winners.Add(fortisRec);
                                                        }
                                                    }

                                                    var secret = TaskWinnerUtility.GetVerifySecret();
                                                    Globals.VerifySecret = secret;

                                                    foreach (var fortis in winners)
                                                    {
                                                        //Give winners time to respond - exactly 3 seconds in total with 100ms response times per.
                                                        try
                                                        {
                                                            await _hubContext.Clients.Client(fortis.Context.ConnectionId).SendAsync("GetAdjMessage", "sendWinningBlock", secret)
                                                                .WaitAsync(new TimeSpan(0, 0, 0, 0, 100));
                                                        }
                                                        catch(Exception ex)
                                                        {

                                                        }
                                                        
                                                    }

                                                    //Give users time for responses to complete. They have 100ms + 3 secs here. Max 30 responses coming
                                                    await Task.Delay(3000);

                                                    var winningBlocks = Globals.TaskWinnerDict;                                                                                                        
                                                    if(winningBlocks.TryGetValue(taskWinner.Address, out var winnersBlock))
                                                    {
                                                        //process winners block
                                                        //1. 
                                                        var signature = await AdjudicatorSignBlock(winnersBlock.WinningBlock.Hash);
                                                        winnersBlock.WinningBlock.AdjudicatorSignature = signature;
                                                        var result = await BlockValidatorService.ValidateBlock(winnersBlock.WinningBlock);
                                                        if(result == true)
                                                        {
                                                            var nextBlock = winnersBlock.WinningBlock;
                                                            ConsoleWriterService.Output("Task Completed and Block Found: " + nextBlock.Height.ToString());
                                                            ConsoleWriterService.Output(DateTime.Now.ToString());
                                                            string data = "";
                                                            data = JsonConvert.SerializeObject(nextBlock);

                                                            ConsoleWriterService.Output("Sending Blocks Now - Height: " + nextBlock.Height.ToString());
                                                            await _hubContext.Clients.All.SendAsync("GetAdjMessage", "taskResult", data);
                                                            ConsoleWriterService.Output("Done sending - Height: " + nextBlock.Height.ToString());

                                                            string taskQuestionStr = "";
                                                            var nTaskQuestion = await TaskQuestionUtility.CreateTaskQuestion("rndNum");
                                                            ConsoleWriterService.Output("New Task Created.");
                                                            Globals.CurrentTaskQuestion = nTaskQuestion;
                                                            TaskQuestion nSTaskQuestion = new TaskQuestion();
                                                            nSTaskQuestion.TaskType = nTaskQuestion.TaskType;
                                                            nSTaskQuestion.BlockHeight = nTaskQuestion.BlockHeight;

                                                            taskQuestionStr = JsonConvert.SerializeObject(nSTaskQuestion);

                                                            await ProcessFortisPool_New(taskAnswerList);
                                                            ConsoleWriterService.Output("Fortis Pool Processed");

                                                            foreach (var answer in Globals.TaskAnswerDict_New.Values)
                                                                if (answer.NextBlockHeight <= nextBlock.Height)
                                                                    Globals.TaskAnswerDict_New.TryRemove(answer.Address, out var test);

                                                            foreach (var answer in Globals.TaskAnswerDict.Values)
                                                                if (answer.Block.Height <= nextBlock.Height)
                                                                    Globals.TaskAnswerDict.TryRemove(answer.Address, out var test);

                                                            foreach (var number in Globals.TaskSelectedNumbers.Values)
                                                                if (number.NextBlockHeight <= nextBlock.Height)
                                                                    Globals.TaskSelectedNumbers.TryRemove(number.Address, out var test);

                                                            foreach (var number in Globals.TaskWinnerDict.Values)
                                                                if (number.WinningBlock.Height <= nextBlock.Height)
                                                                    Globals.TaskWinnerDict.TryRemove(number.Address, out var test);                                                            

                                                            Thread.Sleep(100);

                                                            Globals.VerifySecret = "";

                                                            await _hubContext.Clients.All.SendAsync("GetAdjMessage", "task", taskQuestionStr);
                                                            ConsoleWriterService.Output("Task Sent.");

                                                            findWinner = false;
                                                            taskFindCount = 0;
                                                            Globals.AdjudicateLock = false;
                                                            Globals.LastAdjudicateTime = TimeUtil.GetTime();

                                                            Globals.BroadcastedTrxDict.Clear();

                                                        }
                                                        else
                                                        {
                                                            ConsoleWriterService.Output("Block failed validation");
                                                            if (failedTaskAnswersList == null)
                                                            {
                                                                failedTaskAnswersList = new List<TaskNumberAnswer>();
                                                            }
                                                            failedTaskAnswersList.Add(taskWinner);

                                                            while(findWinner)
                                                            {
                                                                var randChoice = new Random();
                                                                int index = randChoice.Next(winningBlocks.Count());
                                                                //winners block missing, process others randomly
                                                                var randomChosen = winningBlocks.Skip(index).First().Value;

                                                                if (randomChosen != null)
                                                                {
                                                                    winnersBlock = null;
                                                                    winnersBlock = randomChosen;
                                                                    var rSignature = await AdjudicatorSignBlock(winnersBlock.WinningBlock.Hash);
                                                                    winnersBlock.WinningBlock.AdjudicatorSignature = rSignature;
                                                                    var nResult = await BlockValidatorService.ValidateBlock(winnersBlock.WinningBlock);
                                                                    if (nResult == true)
                                                                    {
                                                                        var nextBlock = winnersBlock.WinningBlock;
                                                                        ConsoleWriterService.Output("Task Completed and Block Found: " + nextBlock.Height.ToString());
                                                                        ConsoleWriterService.Output(DateTime.Now.ToString());
                                                                        string data = "";
                                                                        data = JsonConvert.SerializeObject(nextBlock);

                                                                        ConsoleWriterService.Output("Sending Blocks Now - Height: " + nextBlock.Height.ToString());
                                                                        await _hubContext.Clients.All.SendAsync("GetAdjMessage", "taskResult", data);
                                                                        ConsoleWriterService.Output("Done sending - Height: " + nextBlock.Height.ToString());

                                                                        string taskQuestionStr = "";
                                                                        var nTaskQuestion = await TaskQuestionUtility.CreateTaskQuestion("rndNum");
                                                                        ConsoleWriterService.Output("New Task Created.");
                                                                        Globals.CurrentTaskQuestion = nTaskQuestion;
                                                                        TaskQuestion nSTaskQuestion = new TaskQuestion();
                                                                        nSTaskQuestion.TaskType = nTaskQuestion.TaskType;
                                                                        nSTaskQuestion.BlockHeight = nTaskQuestion.BlockHeight;

                                                                        taskQuestionStr = JsonConvert.SerializeObject(nSTaskQuestion);
                                                                        //await ProcessFortisPool_New(taskAnswerList);
                                                                        ConsoleWriterService.Output("Fortis Pool Processed");

                                                                        foreach (var answer in Globals.TaskAnswerDict_New.Values)
                                                                            if (answer.NextBlockHeight <= nextBlock.Height)
                                                                                Globals.TaskAnswerDict_New.TryRemove(answer.Address, out var test);

                                                                        foreach (var answer in Globals.TaskAnswerDict.Values)
                                                                            if (answer.Block.Height <= nextBlock.Height)
                                                                                Globals.TaskAnswerDict.TryRemove(answer.Address, out var test);

                                                                        foreach (var number in Globals.TaskSelectedNumbers.Values)
                                                                            if (number.NextBlockHeight <= nextBlock.Height)
                                                                                Globals.TaskSelectedNumbers.TryRemove(number.Address, out var test);

                                                                        foreach (var number in Globals.TaskWinnerDict.Values)
                                                                            if (number.WinningBlock.Height <= nextBlock.Height)
                                                                                Globals.TaskWinnerDict.TryRemove(number.Address, out var test);

                                                                        Thread.Sleep(100);

                                                                        Globals.VerifySecret = "";

                                                                        await _hubContext.Clients.All.SendAsync("GetAdjMessage", "task", taskQuestionStr);
                                                                        ConsoleWriterService.Output("Task Sent.");

                                                                        findWinner = false;
                                                                        taskFindCount = 0;
                                                                        Globals.AdjudicateLock = false;
                                                                        Globals.LastAdjudicateTime = TimeUtil.GetTime();

                                                                        Globals.BroadcastedTrxDict.Clear();

                                                                    }
                                                                    else
                                                                    {
                                                                        var nTaskNumAnswer = taskAnswerList.Where(x => x.Address == winnersBlock.Address).FirstOrDefault();
                                                                        ConsoleWriterService.Output("Block failed validation");
                                                                        if(nTaskNumAnswer != null)
                                                                        {
                                                                            if (failedTaskAnswersList == null)
                                                                            {
                                                                                failedTaskAnswersList = new List<TaskNumberAnswer>();
                                                                            }
                                                                            failedTaskAnswersList.Add(nTaskNumAnswer);
                                                                        }
                                                                        winningBlocks.TryRemove(randomChosen.Address, out var test);
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        //Selecting the other closest from winning numbers
                                                        //2.
                                                        while(findWinner)
                                                        {
                                                            var randChoice = new Random();
                                                            int index = randChoice.Next(winningBlocks.Count());
                                                            //winners block missing, process others randomly
                                                            var randomChosen = winningBlocks.Skip(index).First().Value;

                                                            if (randomChosen != null)
                                                            {
                                                                winnersBlock = randomChosen;
                                                                var signature = await AdjudicatorSignBlock(winnersBlock.WinningBlock.Hash);
                                                                winnersBlock.WinningBlock.AdjudicatorSignature = signature;
                                                                var result = await BlockValidatorService.ValidateBlock(winnersBlock.WinningBlock);
                                                                if (result == true)
                                                                {
                                                                    var nextBlock = winnersBlock.WinningBlock;
                                                                    ConsoleWriterService.Output("Task Completed and Block Found: " + nextBlock.Height.ToString());
                                                                    ConsoleWriterService.Output(DateTime.Now.ToString());
                                                                    string data = "";
                                                                    data = JsonConvert.SerializeObject(nextBlock);

                                                                    ConsoleWriterService.Output("Sending Blocks Now - Height: " + nextBlock.Height.ToString());
                                                                    await _hubContext.Clients.All.SendAsync("GetAdjMessage", "taskResult", data);
                                                                    ConsoleWriterService.Output("Done sending - Height: " + nextBlock.Height.ToString());

                                                                    string taskQuestionStr = "";
                                                                    var nTaskQuestion = await TaskQuestionUtility.CreateTaskQuestion("rndNum");
                                                                    ConsoleWriterService.Output("New Task Created.");
                                                                    Globals.CurrentTaskQuestion = nTaskQuestion;
                                                                    TaskQuestion nSTaskQuestion = new TaskQuestion();
                                                                    nSTaskQuestion.TaskType = nTaskQuestion.TaskType;
                                                                    nSTaskQuestion.BlockHeight = nTaskQuestion.BlockHeight;

                                                                    taskQuestionStr = JsonConvert.SerializeObject(nSTaskQuestion);
                                                                    //await ProcessFortisPool_New(taskAnswerList);
                                                                    ConsoleWriterService.Output("Fortis Pool Processed");

                                                                    foreach (var answer in Globals.TaskAnswerDict_New.Values)
                                                                        if (answer.NextBlockHeight <= nextBlock.Height)
                                                                            Globals.TaskAnswerDict_New.TryRemove(answer.Address, out var test);

                                                                    foreach (var answer in Globals.TaskAnswerDict.Values)
                                                                        if (answer.Block.Height <= nextBlock.Height)
                                                                            Globals.TaskAnswerDict.TryRemove(answer.Address, out var test);

                                                                    foreach (var number in Globals.TaskSelectedNumbers.Values)
                                                                        if (number.NextBlockHeight <= nextBlock.Height)
                                                                            Globals.TaskSelectedNumbers.TryRemove(number.Address, out var test);

                                                                    foreach (var number in Globals.TaskWinnerDict.Values)
                                                                        if (number.WinningBlock.Height <= nextBlock.Height)
                                                                            Globals.TaskWinnerDict.TryRemove(number.Address, out var test);

                                                                    Thread.Sleep(100);

                                                                    Globals.VerifySecret = "";

                                                                    await _hubContext.Clients.All.SendAsync("GetAdjMessage", "task", taskQuestionStr);
                                                                    ConsoleWriterService.Output("Task Sent.");

                                                                    findWinner = false;
                                                                    taskFindCount = 0;
                                                                    Globals.AdjudicateLock = false;
                                                                    Globals.LastAdjudicateTime = TimeUtil.GetTime();

                                                                    Globals.BroadcastedTrxDict.Clear();

                                                                }
                                                                else
                                                                {
                                                                    var nTaskNumAnswer = taskAnswerList.Where(x => x.Address == winnersBlock.Address).FirstOrDefault();
                                                                    ConsoleWriterService.Output("Block failed validation");
                                                                    if (nTaskNumAnswer != null)
                                                                    {
                                                                        if (failedTaskAnswersList == null)
                                                                        {
                                                                            failedTaskAnswersList = new List<TaskNumberAnswer>();
                                                                        }
                                                                        failedTaskAnswersList.Add(nTaskNumAnswer);
                                                                    }
                                                                    winningBlocks.TryRemove(randomChosen.Address, out var test);
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                ConsoleWriterService.Output("Task Winner was Not Found!");
                                                if (failedTaskAnswersList != null)
                                                {
                                                    List<TaskNumberAnswer> validTaskAnswerList = taskAnswerList.Except(failedTaskAnswersList).ToList();
                                                    if (validTaskAnswerList.Count() == 0)
                                                    {
                                                        ConsoleWriterService.Output("Error in task list");
                                                        //If this happens that means not a single task answer yielded a validatable block.
                                                        //If this happens chain must be corrupt or zero validators are online.
                                                        findWinner = false;
                                                        Globals.AdjudicateLock = false;
                                                    }
                                                }
                                                else
                                                {
                                                    ConsoleWriterService.Output("Task list failed to find winner");
                                                    failedTaskAnswersList = new List<TaskNumberAnswer>();
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        Globals.AdjudicateLock = false;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        //dipose timer.
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
                Console.WriteLine("Client Call Service");
                Globals.AdjudicateLock = false;
            }
        }

        #endregion

        #region Do Work()

        private async void DoWork(object? state)
        {
            if(Globals.LastBlock.Height >= Globals.BlockLock)
            {
                await DoWork_New();
                
            }
            else
            {
                await DoWork_Deprecated();
            }
            
        }

        #endregion

        #region Adjudicator Sign Block 

        private async Task<string> AdjudicatorSignBlock(string message)
        {
            var leadAdj = Globals.LeadAdjudicator;
            var account = AccountData.GetSingleAccount(leadAdj.Address);

            var accPrivateKey = GetPrivateKeyUtility.GetPrivateKey(account.PrivateKey, account.Address);

            BigInteger b1 = BigInteger.Parse(accPrivateKey, NumberStyles.AllowHexSpecifier);//converts hex private key into big int.
            PrivateKey privateKey = new PrivateKey("secp256k1", b1);

            var sig = SignatureService.CreateSignature(message, privateKey, account.PublicKey);

            return sig;
        }

        #endregion

        #region Process Fortis Pool **NEW
        public async Task ProcessFortisPool_New(List<TaskNumberAnswer> taskAnswerList)
        {
            try
            {
                if (taskAnswerList != null)
                {
                    foreach (TaskNumberAnswer taskAnswer in taskAnswerList)
                    {
                        if (Globals.FortisPool.TryGetFromKey2(taskAnswer.Address, out var validator))
                            validator.Value.LastAnswerSendDate = DateTime.Now;
                    }
                }

                var nodeWithAnswer = Globals.FortisPool.Values.Where(x => x.LastAnswerSendDate != null).ToList();
                var deadNodes = nodeWithAnswer.Where(x => x.LastAnswerSendDate.Value.AddMinutes(15) <= DateTime.Now).ToList();
                foreach (var deadNode in deadNodes)
                {
                    Globals.FortisPool.TryRemoveFromKey1(deadNode.IpAddress, out var test);
                    deadNode.Context.Abort();
                }                
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: ClientCallService.ProcessFortisPool: " + ex.ToString());
            }

        }

        #endregion

        #region Process Fortis Pool **Deprecated
        public async Task ProcessFortisPool_Deprecated(List<TaskAnswer> taskAnswerList)
        {
            int errorCountA = 0;
            int errorCountB = 0;
            int errorCountC = 0;
            try
            {
                var pool = Globals.FortisPool;

                if (taskAnswerList != null)
                {
                    foreach (TaskAnswer taskAnswer in taskAnswerList)
                    {
                        try
                        {
                            if(Globals.FortisPool.TryGetFromKey2(taskAnswer.Address, out var validator))
                                validator.Value.LastAnswerSendDate = DateTime.Now;
                        }
                        catch { errorCountB += 1; }
                        
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: ClientCallService.ProcessFortisPool_Deprecated(): " + ex.ToString());
            }

            if(errorCountA > 0 || errorCountB > 0 || errorCountC > 0)
                Console.WriteLine($"Error Count A = {errorCountA} || Error Count B = {errorCountB} || Error Count C = {errorCountC}");
        }

        #endregion

        #region Stop and Dispose

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer.Dispose();
            _fortisPoolTimer.Dispose();
            _blockStateSyncTimer.Dispose();
            _checkpointTimer.Dispose();
        }

        #endregion

        #region Send Message

        public async Task SendMessage(string message, string data)
        {
            await _hubContext.Clients.All.SendAsync("GetAdjMessage", message, data);
        }

        #endregion
    }
}
