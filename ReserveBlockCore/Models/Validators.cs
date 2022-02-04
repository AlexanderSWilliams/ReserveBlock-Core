﻿using LiteDB;
using ReserveBlockCore.Data;

namespace ReserveBlockCore.Models
{
    public class Validators
    {
        public string Address { get; set; }
        public decimal Amount { get; set; } //Must be 1000 or more.
        public long SolvedBlocks { get; set; }
        public long LastBlockSolvedTime { get; set; } //timestamp
        public string NodeIP { get; set; } // this will be used to call out to next node after validator is complete. If node is online it will be chosen next. 
        public class Stake
        {

            public static List<Validators> ValidatorList { get; set; }

            public static void Add(Validators validator)
            {
                var validators = GetAll();

                // insert into database
                validators.Insert(validator);
            }

            public static ILiteCollection<Validators> GetAll()
            {
                var validators = DbContext.DB.GetCollection<Validators>(DbContext.RSRV_VALIDATORS);
                return validators;
            }

            internal static void Initialize()
            {
                ValidatorList = new List<Validators>();
                var staker = GetAll();
                if (staker.Count() < 1)
                {
                    // each account must stake at least 1000. We hard code in a few to get blocks moving. 
                    Add(new Validators
                    {
                        Address = "Address_1",
                        Amount = 1000
                    });

                    Add(new Validators
                    {
                        Address = "Address_2",
                        Amount = 1000
                    });

                    Add(new Validators
                    {
                        Address = "Address_3",
                        Amount = 1000
                    });

                    Add(new Validators
                    {
                        Address = "Address_4",
                        Amount = 1000
                    });

                    ValidatorList.AddRange(GetAll().FindAll());
                }
                else
                {
                    ValidatorList.AddRange(GetAll().FindAll());
                }


            }

            //This will be a more stochastic ordered list. For now just grabbing a random person.
            public static string GetBlockValidator()
            {
                var numOfValidators = ValidatorList.Count;
                var random = new Random();
                int choosed = random.Next(0, numOfValidators);
                var validator = ValidatorList[choosed].Address;
                return validator;
            }

        }
    }

}