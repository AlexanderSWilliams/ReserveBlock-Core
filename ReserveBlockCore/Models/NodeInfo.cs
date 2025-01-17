﻿using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ReserveBlockCore.Models
{
    public class NodeInfo
    {
        public HubConnection Connection;
        public string NodeIP { get; set; } 
        public long NodeHeight { get; set; }
        public int NodeLatency { get; set; }
        public DateTime? NodeLastChecked { get; set; }

        public int IsSendingBlock;
        
        public long SendingBlockTime;
        
        public long TotalDataSent;

        public long PreviousReceiveTime;

        public long SecondPreviousReceiveTime;

        public readonly SemaphoreSlim APILock = new SemaphoreSlim(1, 1);

        public async Task<T> InvokeAsync<T>(string method, object[] args = null, CancellationToken ct = default)
        {
            await APILock.WaitAsync();
            var delay = Task.Delay(1000);
            try
            {
                return await Connection.InvokeCoreAsync<T>(method, args ?? Array.Empty<object>(), ct);
            }
            finally
            {
                await delay;
                if (APILock.CurrentCount == 0)
                    APILock.Release();
            }
        }
    }
}
