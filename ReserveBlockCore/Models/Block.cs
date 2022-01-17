﻿using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ReserveBlockCore.Models
{
    public class Block
    {
		public long Height { get; set; }
		public long Timestamp { get; set; }
		public string Hash { get; set; }
		public string PrevHash { get; set; }
		public string MerkleRoot { get; set; }
		public double TotalAmount { get; set; }
		public string Validator { get; set; }
		public float TotalReward { get; set; }
		public int Difficulty { get; set; }
		public int Version { get; set; }
		public int NumOfTx { get; set; }
		public long Size { get; set; }
		public int BCraftTime { get; set; }

		public IList<Transaction> Transactions { get; set; }
		//Methods
		public void Build()
		{
			Version = 1;
			NumOfTx = Transactions.Count;
			TotalAmount = GetTotalAmount();
			TotalReward = GetTotalFees();
			MerkleRoot = GetMerkleRoot();
			PrevHash = GetLastBlack() != null ? GetLastBlack().Hash : "Genesis Block"; //This is done because chain starting there won't be a previous hash. 
			Hash = GetBlockHash();
			Difficulty = 1;
		}
		public int NumberOfTransactions
		{
			get { return Transactions.Count(); }
		}
		private float GetTotalFees()
		{
			var totFee = Transactions.AsEnumerable().Sum(x => x.Fee);
			return (float)totFee;
		}
		public static ILiteCollection<Block> GetBlocks()
		{
			var block = DbContext.DB.GetCollection<Block>(DbContext.RSRV_BLOCKS);
			block.EnsureIndex(x => x.Height);
			return block;
		}
		private static Block GetLastBlack()
		{
			var blockchain = GetBlocks();
			var block = blockchain.FindOne(Query.All(Query.Descending));
			return block;
		}
		private double GetTotalAmount()
		{
			var totalAmount = Transactions.AsEnumerable().Sum(x => x.Amount);
			return (double)totalAmount;
		}
		public string GetBlockHash()
		{
			var strSum = Version + PrevHash + MerkleRoot + Timestamp + Difficulty + Validator;
			var hash = HashingService.GenerateHash(strSum);
			return hash;
		}

		private string GetMerkleRoot()
		{
			// List<Transaction> txList = JsonConvert.DeserializeObject<List<Transaction>>(jsonTxs);
			var txsHash = new List<string>();

			Transactions.ToList().ForEach(x => { txsHash.Add(x.Hash); });

			var hashRoot = MerkleService.CreateMerkleRoot(txsHash.ToArray());
			return hashRoot;
		}
	}
}
