﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WalletMiddleware.TableModels;
using WalletContracts.Entity;
using WalletMiddleware.Apis;
using WalletMiddleware.Apis.Wcf;
using WalletMiddleware.Apis.Utils;

using WalletMiddleware.ApisJava.vo;


using System.Threading;


namespace WalletMiddleware.Dal
{
    /// <summary>
    /// 热钱包 Dal 
    /// 改用中间件数据库
    /// </summary>
    public class HotWalletDal //: DbBaseRiskCheck<TableModels.HotWallet> //DbBaseEbChange<TableModels.HotWallet>
    {
        public static readonly HotWalletDal Instane = new HotWalletDal();
        public HotWalletDal()
        {

        }
        /// <summary>
        /// 2019-8-19
        /// 更热钱包的job具体业务
        /// 1.从交易所查出所有HotWallet中的数据。
        /// 2.写到内存中，按WalletMiddleware.TableModels.HotWallet 对象保存。注 ischange为false.
        /// 3.遍历 若status=0时，是新增加的钱包，需要钱包创建热地址和wdwu,得到后 ，把ischange设为true.
        ///        若Status=1时，是正常的数据，需要通过钱包得到冷热金额，若和内存不相同，更新内存冷热金额，把ischange设为true,
        /// 4.最后把内存中的 ischange=true的数据，提交到交易所aPi.
        /// 5.若热钱包的余额低于CalcTips值时，就写入本地警报。
        /// </summary>
        /// <returns></returns>
        public string UpdateHotWallet(string ignoreCoin)
        {
            StringBuilder sbRz = new StringBuilder();
            string coinType = string.Empty;
            try
            {
                //获取HotWallet
                List<HotWallet> hotWallets = Api.Instance.GetData();//.Select(x => { x.IsChange = false; return x; }).ToList(); // hotWallets = hotWallets.Skip(100).ToList();

                //2020-10-27 排除 GRTC 
                hotWallets = hotWallets.Where(i => i.CoinType != ignoreCoin).ToList();
                if (hotWallets.Any())
                {
                    int iIdex = 0;
                    foreach (var x in hotWallets)
                    {
                        x.IsChange = false;
                        coinType = x.CoinType;
                        try
                        {

                            if (x.Status == 0)
                            {
                                //新增加的钱包，需要钱包创建热地址和wdwu,得到后 ，把ischange设为true
                                DataReturn<Addresses> sAddr = Wcf.Instance.CreateHotAddress(x.CoinType);
                                if (!Equals(sAddr, null) && !Equals(sAddr.Data, null)  && sAddr.IsSucess)
                                {
                                    if (sAddr.Data.IsSuccess)
                                    {
                                        x.Address = sAddr.Data.Addr;
                                        x.MdWu = sAddr.MdWu;
                                        //x.Status = 1;
                                        x.IsChange = true;
                                    }
                                    else
                                    {
                                        sbRz.Append($"第{iIdex}_{x.CoinType}个创建地址返回失败结果");
                                    }

                                }
                                else
                                {
                                    sbRz.Append($"第{iIdex}_{x.CoinType}个创建地址返回null结果,钱包可能不存在当前币种;");
                                }
                            }
                            else
                            {
                                //正常的数据，需要通过钱包得到冷热金额，若和内存不相同，更新内存冷热金额，把ischange设为true
                                #region 热地址余额 
                                Thread.Sleep(500);
                                var moneyHot = Wcf.Instance.GetBalance(x.CoinType, x.Address);
                                if (!Equals(moneyHot, null) && moneyHot.IsSucess)
                                {
                                    x.IsChange = x.Amount.ToString() != moneyHot.Data.ToString();
									if (moneyHot.Data == 0M)
									{
										x.IsChange = false;
									}
									//XS.Core.Log.InfoLog.Info($"{x.CoinType}更新热余额：{x.IsChange}，原：{x.Amount}现：{moneyHot.Data}");
									if (x.IsChange)
									{
										x.Amount = decimal.Parse(moneyHot.Data.ToString());
									}
                                    
                                }
                                else
                                {
                                    sbRz.AppendFormat($"获取热钱包第{iIdex}个地址更新金额返回失败结果;");
                                }
                                #endregion
                                #region 冷钱包余额
                                Thread.Sleep(500);
                                var moneyCold = Wcf.Instance.GetBalance(x.CoinType, "", true);

                                if (!Equals(moneyCold, null) && moneyCold.IsSucess)
                                {
                                    if (!x.IsChange)
                                    {
                                        x.IsChange = x.ColdAmount.ToString() != moneyCold.Data.ToString();
                                    }
                                    //XS.Core.Log.InfoLog.Info($"{x.CoinType}更新冷余额：{x.IsChange}，原：{x.Amount}现：{moneyCold.Data}");
                                    x.ColdAmount = decimal.Parse(moneyCold.Data.ToString());
                                }
                                else
                                {
                                    sbRz.Append($"获取冷钱包第{iIdex}个地址更新金额返回失败结果;");
                                }
                                #endregion

                                if (x.CalcTips != 0 && x.Amount < x.CalcTips &&
                                           (x.LastCalcTipTime == DateTime.MinValue || x.LastCalcTipTime == DateTime.MaxValue || x.LastCalcTipTime.AddHours(1) < DateTime.Now))
                                {
                                    // 报警，当前的余额低于设定值
                                    Dal.SendMsgsDal.Instane.SendEmails("热地址里没钱啦，快看看吧", x.CoinType + "币当前余额：" + x.Amount + "，小于设定值：" + x.CalcTips);
                                    x.LastCalcTipTime = DateTime.Now;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            x.IsChange = true;
                            x.Amount = 0;
                            x.ColdAmount = 0;
                            sbRz.AppendFormat("HotWallet-{0}通过Wcf更新地址和冷热余额事出现异常:{1}.", coinType, ex.Message);
                            XS.Core.Log.ErrorLog.ErrorFormat("HotWallet-{0}通过Wcf更新地址和冷热余额事出现异常:{1}_{2}.", coinType, ex.Message, ex.StackTrace);
                        }
                        iIdex++;
                    }
                }
                var sendData = hotWallets.Where(x => x.IsChange).ToList();
                var objList = new List<object>();
                if (sendData.Any())
                {
                    sendData.ForEach(x =>
                    {
                        objList.Add(new
                        {
                            x.Id,
                            x.Address,
                            x.MdWu,
                            x.ColdAmount,
                            x.Amount
                        });
                    });
                    //通知交易所，发生改变的数据
                    var data = JsonUtils.Serialize(objList);
                    var (result, err) = Api.Instance.UpdateHotWallet(data);
                    if (result)
                    {
                        sbRz.Append("HotWallet成功通知交易所.");
                    }
                    else
                    {
                        sbRz.Append($"HotWallet通知交易所修改数据失败:{err}.");
                    }
                }



            }
            catch (Exception ex)
            {
                XS.Core.Log.ErrorLog.ErrorFormat("{2}-热钱包任务发生异常:{0}-{1}", ex.Message, ex.StackTrace, coinType);
                sbRz.AppendFormat("{1}-热钱包任务发生异常:{0}", ex.Message, coinType);
            }
            return sbRz.ToString();
        }




        /// <summary>
        /// 新版 2019-11-22 YHL
        /// 调用java 开放Api。
        /// </summary>
        /// <returns></returns>
        public string UpdateHotWalletNew()
        {
            StringBuilder sbRz = new StringBuilder();
            string coinType = string.Empty;
            try
            {
                //获取HotWallet
                List<HotWallet> hotWallets = Api.Instance.GetData();//.Select(x => { x.IsChange = false; return x; }).ToList();
                if (hotWallets.Any())
                {
                    //2019-11-22 获得热钱包数据
                    List<ReciveHotWalletVo> _hotWallets= ApisJava.JWalletBll.Instane.GetHotWallets();
                    foreach (var x in _hotWallets)
                    {
                        //币种 + 热地址 + 用户ID + 热金额[8位小数] + 冷金额[8位小数] + 用户私钥 + 延值
                        string _mdwu = Md5Utils.ToMd5AndExtend($"{x.CoinType}{x.Addr}" +
                            $"{Settings.Instance.WcfUserId }{x.HotSum.ToString("0.########")}{x.CoolSum.ToString("0.########")}" +
                            $"{Settings.Instance.PrivateKey}");


                        if (x.MdWu == _mdwu)
                        {
                            string aa = "相等";
                        }

                    }


                    int iIdex = 0;
                    foreach (var x in hotWallets)
                    {
                        x.IsChange = false;
                        coinType = x.CoinType;
                        try
                        {

                            if (x.Status == 0)
                            {
                                //新增加的钱包，需要钱包创建热地址和wdwu,得到后 ，把ischange设为true
                                //DataReturn<Addresses> sAddr = Wcf.Instance.CreateHotAddress(x.CoinType);

                                ReciveHotWalletVo addHotWallet= _hotWallets.Where(i => i.CoinType == x.CoinType).First();
                                if (!Equals(addHotWallet, null) )
                                {
                                    //币种+热地址+用户ID+热金额 [8位小数]+冷金额[8位小数] + 用户私钥 +延值
                                    string _mdwu = Md5Utils.ToMd5AndExtend($"{x.CoinType}{addHotWallet.Addr}" +
                                        $"{Settings.Instance.WcfUserId }{addHotWallet.HotSum.ToString("0.########")}{addHotWallet.CoolSum.ToString("0.########")}" +
                                        $"{Settings.Instance.PrivateKey}");

                                   
                                    if (addHotWallet.MdWu==_mdwu)
                                    {
                                        x.Address = addHotWallet.Addr;
                                        //x.MdWu = sAddr.MdWu;
                                        //x.Status = 1;
                                        x.IsChange = true;
                                    }
                                    else
                                    {
                                        sbRz.Append($"第{iIdex}_{x.CoinType}个创建地址返回失败结果");
                                    }

                                }
                                else
                                {
                                    sbRz.Append($"第{iIdex}_{x.CoinType}个创建地址返回null结果,钱包可能不存在当前币种;");
                                }
                            }
                            else
                            {
                                //正常的数据，需要通过钱包得到冷热金额，若和内存不相同，更新内存冷热金额，把ischange设为true
                                              
                                //var moneyHot = Wcf.Instance.GetBalance(x.CoinType, x.Address);
                                ReciveHotWalletVo moneyHot= _hotWallets.Where(i => i.CoinType == x.CoinType && i.Addr == x.Address).First();
                                if (!Equals(moneyHot, null) )
                                {
                                    //币种+热地址+用户ID+热金额 [8位小数]+冷金额[8位小数] + 用户私钥 +延值
                                    string _mdwu = Md5Utils.ToMd5AndExtend($"{x.CoinType}{moneyHot.Addr}" +
                                        $"{Settings.Instance.WcfUserId }{moneyHot.HotSum.ToString("0.########")}{moneyHot.CoolSum.ToString("0.########")}" +
                                        $"{Settings.Instance.PrivateKey}");


                                    if (moneyHot.MdWu == _mdwu)
                                    {
                                        #region 热地址余额     
                                        x.IsChange = x.Amount.ToString() != moneyHot.HotSum.ToString();                                        
                                        x.Amount =moneyHot.HotSum;
                                        #endregion
                                        #region 冷钱包余额
                                        x.IsChange = x.ColdAmount.ToString() != moneyHot.CoolSum.ToString();
                                        x.ColdAmount = moneyHot.CoolSum;
                                        #endregion
                                    }

                                }
                                else
                                {
                                    sbRz.AppendFormat($"获取热钱包第{iIdex}个地址更新金额返回失败结果;");
                                }
                             
                                

                                if (x.CalcTips != 0 && x.Amount < x.CalcTips &&
                                           (x.LastCalcTipTime == DateTime.MinValue || x.LastCalcTipTime == DateTime.MaxValue || x.LastCalcTipTime.AddHours(1) < DateTime.Now))
                                {
                                    // 报警，当前的余额低于设定值
                                    Dal.SendMsgsDal.Instane.SendEmails("热地址里没钱啦，快看看吧", x.CoinType + "币当前余额：" + x.Amount + "，小于设定值：" + x.CalcTips);
                                    x.LastCalcTipTime = DateTime.Now;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            x.IsChange = true;
                            x.Amount = 0;
                            x.ColdAmount = 0;
                            sbRz.AppendFormat("HotWallet-{0}通过Wcf更新地址和冷热余额事出现异常:{1}.", coinType, ex.Message);
                            XS.Core.Log.ErrorLog.ErrorFormat("HotWallet-{0}通过Wcf更新地址和冷热余额事出现异常:{1}_{2}.", coinType, ex.Message, ex.StackTrace);
                        }
                        iIdex++;


                    }
                }

                var sendData = hotWallets.Where(x => x.IsChange).ToList();
                var objList = new List<object>();
                if (sendData.Any())
                {
                    sendData.ForEach(x =>
                    {
                        objList.Add(new
                        {
                            x.Id,
                            x.Address,
                            //x.MdWu,
                            x.ColdAmount,
                            x.Amount
                        });
                    });
                    //通知交易所，发生改变的数据
                    var data = JsonUtils.Serialize(objList);
                    var (result, err) = Api.Instance.UpdateHotWallet(data);
                    if (result)
                    {
                        sbRz.Append("HotWallet成功通知交易所.");
                    }
                    else
                    {
                        sbRz.Append($"HotWallet通知交易所修改数据失败:{err}.");
                    }
                }



            }
            catch (Exception ex)
            {
                XS.Core.Log.ErrorLog.ErrorFormat("{2}-热钱包任务发生异常:{0}-{1}", ex.Message, ex.StackTrace, coinType);
                sbRz.AppendFormat("{1}-热钱包任务发生异常:{0}", ex.Message, coinType);
            }
            return sbRz.ToString();
        }


        #region  不用的代码
        //   /// <summary>
        //   /// 添加新的币种热钱包地址
        //   /// 从交易所采集 添加的新币。
        //   /// 写到中间件中，issend=0.
        //   /// </summary>
        //   /// <returns></returns>
        //   public string InitHotWallet()
        //   {
        //       List<HotWallet> data = Apis.CoinBig.Instance.GetData();
        //       //添加到数据库
        //       foreach (var item in data)
        //       {
        //           //判断是否存在当前币兑
        //           var entity = HotWalletDal.Instane.GetEntityByWhere($"CoinId={item.CoinId}");
        //           if (!IsHaveCoinType(item.CoinId))
        //           {
        //               item.IsSend = false;
        //               Add(item);
        //           }
        //       }
        //       #region old
        //       //       foreach (var model in AppStaticData.CoinTypes)
        //       //       {

        //       //           if (!IsHaveCoinType(model.Key)) //if (!IsHaveCoinType(model.Value.fShortName.Trim()))
        //       //           {
        //       //               TableModels.HotWallet mdWallet = new HotWallet();
        //       //               mdWallet.CoinName = model.Value.fName;
        //       //               mdWallet.CoinId = model.Value.fid;
        //       //               mdWallet.CreateTime = DateTime.Now;
        //       //               mdWallet.CoinType = model.Value.fShortName.Trim();
        //       //               mdWallet.Status = 0;
        //       //mdWallet.LastCalcTipTime = DateTime.MinValue;
        //       ////mdWallet.MdWu =XS.Core.Md5Helper.MD5(mdWallet.CoinType);
        //       //Add(mdWallet);

        //       //           }
        //       //           //model.fShortName  跟钱包那边的cointype是对应的


        //       //       }
        //       #endregion

        //       return "热钱包初始化完成";
        //   }
        //   /// <summary>
        //   /// 创建新的币种钱包地址
        //   /// 1.读出取中间件的HotWallet表中，标记未发送 issend=0的数据，通过wcf把Address,Mdwu 值得到。
        //   /// 2.告诉交易所 通过wcf得到的数据，发送后，标记 issend=1，同时更新到中间件数据库中。
        //   /// </summary>
        //   /// <returns></returns>
        //   public string CreatWalletAddress()
        //   {
        //       StringBuilder sbRz = new StringBuilder();
        //       List<HotWallet> lst = GetList("Status=0");
        //       int iIdex = 0;
        //       foreach (var model in lst)
        //       {
        //           string sUserId = Settings.Instance.WcfUserId;
        //           string sMdwu = HashHelper.GetHashStr(string.Format("coinType={0}&userid={1}", model.CoinType,sUserId));
        //           DataReturn<Addresses> sAddr =  WcfInst.Instance.CreateAddress(sUserId, model.CoinType, sMdwu);
        //           if (!Equals(sAddr,null)&&sAddr.IsSucess)
        //           {
        //               if (sAddr.Data.IsSuccess)
        //               {
        //                   model.Address = sAddr.Data.Addr;
        //                   model.MdWu = sAddr.MdWu;// model.GetMdWu(model.MdWu);
        //                   model.Status = 1;
        //                   Update(model);
        //               }
        //               else
        //               {
        //                   sbRz.AppendFormat("第{0}个创建地址返回失败结果",iIdex);
        //                   //Dal.SendMsgsDal.Instane.SendEmails(sbRz.ToString(), sbRz.ToString());
        //               }

        //           }
        //           else
        //           {
        //               sbRz.AppendFormat("第{0}个创建地址返回null结果;",iIdex);
        //               //Dal.SendMsgsDal.Instane.SendEmails(sbRz.ToString(), sbRz.ToString());
        //           }


        //           iIdex++;
        //       }
        //       return sbRz.ToString();

        //   }
        //   /// <summary>
        //   /// 从wcf中查出最新的钱包数据。
        //   /// 2019-8-19
        //   /// </summary>
        //   /// <returns></returns>
        //public string UpdateMoney()
        //{
        // StringBuilder sbRz = new StringBuilder();
        // List<HotWallet> lst = GetList("Status=1");
        // int iIdex = 0;
        // foreach (var model in lst)
        // {
        //           string sMdwu =
        //               HashHelper.GetHashStr(string.Format("addr={0}&coinType={1}&cold={2}", model.Address, model.CoinType, false));

        //           #region 更新热钱包余额

        //           //string coinType, string addr, bool cold, string signature)
        //           var moneyHot = WcfInst.Instance.GetBalance(model.CoinType, model.Address, false, sMdwu);
        //           bool isHaveData = false;
        //           if (!Equals(moneyHot, null) && moneyHot.IsSucess)
        //           {
        //               model.Amount = decimal.Parse(moneyHot.Data.ToString());
        //               isHaveData = true;

        //           }
        //           else
        //           {
        //               sbRz.AppendFormat("获取热钱包第{0}个地址更新金额返回失败结果;", iIdex);

        //               //Dal.SendMsgsDal.Instane.SendEmails(sbRz.ToString(), sbRz.ToString());
        //           }

        //           string sMdwuCold =
        //               HashHelper.GetHashStr(string.Format("addr={0}&coinType={1}&cold={2}", "", model.CoinType, true));
        //           var moneyCold = WcfInst.Instance.GetBalance(model.CoinType, "", true, sMdwuCold);

        //           if (!Equals(moneyCold, null) && moneyCold.IsSucess)
        //           {
        //               model.ColdAmount = decimal.Parse(moneyCold.Data.ToString());
        //               isHaveData = true;

        //           }
        //           else
        //           {
        //               sbRz.AppendFormat("获取冷钱包第{0}个地址更新金额返回失败结果;", iIdex);
        //           }

        //           if (isHaveData)
        //           {

        //               var temp = new List<Apis.Models.HotWalletUpdateRequest>();
        //               temp.Add(new Apis.Models.HotWalletUpdateRequest() {
        //                   Id = model.CoinId,
        //                   Amount = model.Amount,
        //                   ColdAmount = model.ColdAmount
        //               });
        //               var result = Apis.CoinBig.Instance.Update(temp);

        //               if (result)
        //               {
        //                   //同时把 热金额，冷金额 写入表中。
        //                   Update(model);
        //               }
        //               else
        //               {
        //                   sbRz.AppendFormat("{0}通知交易所更新冷热钱包余额，返回失败;", model.CoinName);
        //               }
        //           }

        //           if (model.CalcTips != 0
        //               && model.Amount < model.CalcTips
        //               && (model.LastCalcTipTime == DateTime.MinValue || model.LastCalcTipTime == DateTime.MaxValue ||
        //                   model.LastCalcTipTime.AddHours(1) < DateTime.Now))
        //           {
        //               //报警，当前的余额低于设定值
        //               Dal.SendMsgsDal.Instane.SendEmails("热地址里没钱啦，快看看吧",
        //                   model.CoinType + "币当前余额：" + model.Amount + "，小于设定值：" + model.CalcTips);
        //               model.LastCalcTipTime = DateTime.Now;
        //               Update(model);
        //           }
        //           #endregion
        //           #region old                
        //           //         //string sUserId = Settings.Instance.WcfUserId;
        //           //         string sMdwu =
        //           // HashHelper.GetHashStr(string.Format("addr={0}&coinType={1}&cold={2}", model.Address, model.CoinType, false));

        //           //#region 更新热钱包余额

        //           ////string coinType, string addr, bool cold, string signature)
        //           //var moneyHot = WcfInst.Instance.GetBalance(model.CoinType, model.Address, false, sMdwu);
        //           //bool isHaveData = false;
        //           //if (!Equals(moneyHot, null) && moneyHot.IsSucess)
        //           //{
        //           // model.Amount = decimal.Parse(moneyHot.Data.ToString());
        //           // isHaveData = true;

        //           //}
        //           //else
        //           //{
        //           // sbRz.AppendFormat("获取热钱包第{0}个地址更新金额返回失败结果;", iIdex);

        //           // //Dal.SendMsgsDal.Instane.SendEmails(sbRz.ToString(), sbRz.ToString());
        //           //}

        //           //         string sMdwuCold =
        //           //             HashHelper.GetHashStr(string.Format("addr={0}&coinType={1}&cold={2}","", model.CoinType, true));
        //           //         var moneyCold = WcfInst.Instance.GetBalance(model.CoinType, "", true, sMdwuCold);

        //           //if (!Equals(moneyCold, null) && moneyCold.IsSucess)
        //           //{
        //           // model.ColdAmount = decimal.Parse(moneyCold.Data.ToString());
        //           // isHaveData = true;

        //           //}
        //           //else
        //           //{
        //           // sbRz.AppendFormat("获取冷钱包第{0}个地址更新金额返回失败结果;", iIdex);
        //           //}

        //           //if (isHaveData)
        //           //{
        //           // Update(model);

        //           //}

        //           //if (model.CalcTips != 0
        //           //    && model.Amount < model.CalcTips
        //           //    && (model.LastCalcTipTime == DateTime.MinValue || model.LastCalcTipTime == DateTime.MaxValue ||
        //           //        model.LastCalcTipTime.AddHours(1) < DateTime.Now))
        //           //{
        //           // //报警，当前的余额低于设定值
        //           // Dal.SendMsgsDal.Instane.SendEmails("热地址里没钱啦，快看看吧",
        //           //  model.CoinType + "币当前余额：" + model.Amount + "，小于设定值：" + model.CalcTips);
        //           // model.LastCalcTipTime = DateTime.Now;
        //           // Update(model);
        //           //}

        //           //         #endregion

        //           #endregion
        //           iIdex++;
        // }

        //       return sbRz.ToString();
        //}

        //public bool IsHaveCoinType(int CoinTypeId)
        //   {
        //       return Exists(string.Format("CoinId={0}", CoinTypeId));
        //       //return Exists(string.Format("CoinType='{0}'", CoinType));
        //   }
        #endregion
    }
}
