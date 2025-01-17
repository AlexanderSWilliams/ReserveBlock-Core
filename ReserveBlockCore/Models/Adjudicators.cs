﻿using ReserveBlockCore.Extensions;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Models
{
    public class Adjudicators
    {
        public long Id { get; set; }
        public string Address { get; set; }
        public string UniqueName { get; set; }
        public string Signature { get; set; }
        public bool IsActive { get; set; }
        public bool IsLeadAdjuidcator { get; set; }
        public string NodeIP { get; set; }
        public string WalletVersion { get; set; }
        public DateTime LastChecked { get; set; }

        public class AdjudicatorData
        {
            public static LiteDB.ILiteCollection<Adjudicators> GetAll()
            {
                try
                {
                    var adjudicators = DbContext.DB_Peers.GetCollection<Adjudicators>(DbContext.RSRV_ADJUDICATORS);
                    return adjudicators;
                }
                catch (Exception ex)
                {
                    DbContext.Rollback();
                    ErrorLogUtility.LogError(ex.ToString(), "Adjudicators.GetAll()");
                    return null;
                }

            }

            public static Adjudicators? GetLeadAdjudicator()
            {
                var adjudicators = GetAll();
                if (adjudicators.Count() > 0)
                {
                    if (Globals.IsTestNet == true)
                    {
                        var leaderTest = adjudicators.FindOne(x => x.IsLeadAdjuidcator == true && x.Address.StartsWith("x"));
                        if(leaderTest != null)
                        {
                            return leaderTest;
                        }
                    }
                
                    var leader = adjudicators.FindOne(x => x.IsLeadAdjuidcator == true);

                    if(leader != null)
                    {
                        return leader;
                    }
                }

                return null;
            }

            public static Guid GetAdjudicatorKey()
            {
                return Guid.NewGuid();
            }
        }

    }
}
