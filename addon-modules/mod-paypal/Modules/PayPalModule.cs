/*
 * Copyright (c) 2009 Adam Frisby (adam@deepthink.com.au), Snoopy Pfeffer (snoopy.pfeffer@yahoo.com)
 *
 * Copyright (c) 2010 BlueWall Information Technologies, LLC
 * James Hughes (jamesh@bluewallgroup.com)
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Web;
using System.Threading;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework.Servers;
using OpenSim.Server.Base;
using OpenSim.Region.CoreModules.World.Land;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using Nwc.XmlRpc;

using Mono.Addins;

[assembly: Addin("PayPal", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace PayPal
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class PayPalModule : ISharedRegionModule, IMoneyModule
    {
        private string m_ppurl = "www.paypal.com";
        // Change to www.sandbox.paypal.com for testing.
        private bool m_active;
        private bool m_enabled;

        private readonly object m_setupLock = new object ();
        private bool m_setup;

        private static readonly ILog m_log = LogManager.GetLogger (MethodBase.GetCurrentMethod ().DeclaringType);
        private readonly Dictionary<UUID, string> m_usersemail = new Dictionary<UUID, string> ();

        private IConfigSource m_config;

        private readonly List<Scene> m_scenes = new List<Scene> ();

        private const int m_maxBalance = 100000;

        private readonly Dictionary<UUID, PayPalTransaction> m_transactionsInProgress =
            new Dictionary<UUID, PayPalTransaction> ();

        private bool m_allowGridEmails = false;
        private bool m_allowGroups = false;
        private bool m_balanceOnEntry = true;
        private string m_messageOnEntry = "PayPal Money System:  OS$ 100 = US$ 1.00";
        private int m_messageDelayAtLogin = 7000;  // 7 seconds

        /// <summary>
        /// Scenes by Region Handle
        /// </summary>
        private Dictionary<ulong, Scene> m_scenel = new Dictionary<ulong, Scene> ();

        // private int m_stipend = 1000;
        private float EnergyEfficiency = 0f;
        private int ObjectCount = 0;
        private int PriceEnergyUnit = 0;
        private int PriceGroupCreate = 0;
        private int PriceObjectClaim = 0;
        private float PriceObjectRent = 0f;
        private float PriceObjectScaleFactor = 0f;
        private int PriceParcelClaim = 0;
        private float PriceParcelClaimFactor = 0f;
        private int PriceParcelRent = 0;
        private int PricePublicObjectDecay = 0;
        private int PricePublicObjectDelete = 0;
        private int PriceRentLight = 0;
        private int PriceUpload = 0;
        private int TeleportMinPrice = 0;
        private float TeleportPriceExponent = 0f;

        #region Currency - PayPal

        /// <summary>
        /// 
        /// </summary>
        /// <remarks>Thanks to Melanie for reminding me about 
        /// EventManager.OnMoneyTransfer being the critical function,
        /// and not ApplyCharge.</remarks>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void OnMoneyTransfer (object sender, EventManager.MoneyTransferArgs e)
        {
            if (!m_active)
                return;
            
            IClientAPI user = null;
            Scene scene = null;
            
            // Find the user's controlling client.
            lock (m_scenes) {
                foreach (Scene sc in m_scenes) {
                    ScenePresence av = sc.GetScenePresence (e.sender);
                    
                    if ((av != null) && (av.IsChildAgent == false)) {
                        // Found the client,
                        // and their root scene.
                        user = av.ControllingClient;
                        scene = sc;
                    }
                }
            }
            
            if (scene == null || user == null) {
                m_log.Warn ("[PayPal] Unable to find scene or user! Aborting transaction.");
                return;
            }
            
            PayPalTransaction txn;
            
            if (e.transactiontype == 5008) {
                // Object was paid, find it.
                SceneObjectPart sop = scene.GetSceneObjectPart (e.receiver);
                if (sop == null) {
                    m_log.Warn ("[PayPal] Unable to find SceneObjectPart that was paid. Aborting transaction.");
                    return;
                }
                
                string email;
                
                if (sop.OwnerID == sop.GroupID) {
                    if (m_allowGroups) {
                        if (!GetEmail (scene.RegionInfo.ScopeID, sop.OwnerID, out email)) {
                            m_log.Warn ("[PayPal] Unknown email address of group " + sop.OwnerID);
                            return;
                        }
                    } else {
                        m_log.Warn ("[PayPal] Payments to group owned objects is disabled.");
                        return;
                    }
                } else {
                    if (!GetEmail (scene.RegionInfo.ScopeID, sop.OwnerID, out email)) {
                        m_log.Warn ("[PayPal] Unknown email address of user " + sop.OwnerID);
                        return;
                    }
                }
                
                m_log.Info ("[PayPal] Start: " + e.sender + " wants to pay object " + e.receiver + " owned by " +
                            sop.OwnerID + " with email " + email + " US$ cents " + e.amount);
                
                txn = new PayPalTransaction (e.sender, sop.OwnerID, email, e.amount, scene, e.receiver,
                                             e.description + " T:" + e.transactiontype,
                                             PayPalTransaction.InternalTransactionType.Payment);
            } else {
                // Payment to a user.
                string email;
                if (!GetEmail (scene.RegionInfo.ScopeID, e.receiver, out email)) {
                    m_log.Warn ("[PayPal] Unknown email address of user " + e.receiver);
                    return;
                }
                
                m_log.Info ("[PayPal] Start: " + e.sender + " wants to pay user " + e.receiver + " with email " +
                            email + " US$ cents " + e.amount);
                
                txn = new PayPalTransaction (e.sender, e.receiver, email, e.amount, scene, e.description + " T:" +
                                             e.transactiontype, PayPalTransaction.InternalTransactionType.Payment);
            }
            
            // Add transaction to queue
            lock (m_transactionsInProgress)
                m_transactionsInProgress.Add (txn.TxID, txn);
            
            string baseUrl = m_scenes[0].RegionInfo.ExternalHostName + ":" + m_scenes[0].RegionInfo.HttpPort;
            
            user.SendLoadURL ("PayPal", txn.ObjectID, txn.To, false, "Confirm payment?", "http://" +
                              baseUrl + "/pp/?txn=" + txn.TxID);
        }

        void TransferSuccess (PayPalTransaction transaction)
        {
            if (transaction.InternalType == PayPalTransaction.InternalTransactionType.Payment) {
                if (transaction.ObjectID == UUID.Zero) {
                    // User 2 User Transaction
                    m_log.Info ("[PayPal] Success: " + transaction.From + " did pay user " +
                                transaction.To + " US$ cents " + transaction.Amount);
                    
                    IUserAccountService userAccountService = m_scenes[0].UserAccountService;
                    UserAccount ua;
                    
                    // Notify receiver
                    ua = userAccountService.GetUserAccount (transaction.From, "", "");
                    SendInstantMessage (transaction.To, ua.FirstName + " " + ua.LastName +
                                        " did pay you US$ cent " + transaction.Amount);
                    
                    // Notify sender
                    ua = userAccountService.GetUserAccount (transaction.To, "", "");
                    SendInstantMessage (transaction.From, "You did pay " + ua.FirstName + " " +
                                        ua.LastName + " US$ cent " + transaction.Amount);
                } else {
                    if (OnObjectPaid != null) {
                        m_log.Info ("[PayPal] Success: " + transaction.From + " did pay object " +
                                    transaction.ObjectID + " owned by " + transaction.To +
                                    " US$ cents " + transaction.Amount);
                        
                        OnObjectPaid (transaction.ObjectID, transaction.From, transaction.Amount);
                    }
                }
            } else if (transaction.InternalType == PayPalTransaction.InternalTransactionType.Purchase) {
                if (transaction.ObjectID == UUID.Zero) {
                    m_log.Error ("[PayPal] Unable to find Object bought! UUID Zero.");
                } else {
                    Scene s = LocateSceneClientIn (transaction.From);
                    SceneObjectPart part = s.GetSceneObjectPart (transaction.ObjectID);
                    if (part == null) {
                        m_log.Error ("[PayPal] Unable to find Object bought! UUID = " + transaction.ObjectID);
                        return;
                    }
                    
                    m_log.Info ("[PayPal] Success: " + transaction.From + " did buy object " +
                                transaction.ObjectID + " from " + transaction.To + " paying US$ cents " +
                                transaction.Amount);
                    
                    IBuySellModule module = s.RequestModuleInterface<IBuySellModule> ();
                    if (module == null) {
                        m_log.Error ("[PayPal] Missing BuySellModule! Transaction failed.");
                    } else {
                        ScenePresence sp = s.GetScenePresence(transaction.From);
                        if (sp != null)
                            module.BuyObject (sp.ControllingClient,
                                          transaction.InternalPurchaseFolderID, part.LocalId,
                                          transaction.InternalPurchaseType, transaction.Amount);
                    }
                }
            } else if (transaction.InternalType == PayPalTransaction.InternalTransactionType.Land) {
                // User 2 Land Transaction
                EventManager.LandBuyArgs e = transaction.E;
                
                lock (e) {
                    e.economyValidated = true;
                }
                
                Scene s = LocateSceneClientIn (transaction.From);
                ILandObject land = s.LandChannel.GetLandObject ((int)e.parcelLocalID);
                
                if (land == null) {
                    m_log.Error ("[PayPal] Unable to find Land bought! UUID = " + e.parcelLocalID);
                    return;
                }
                
                m_log.Info ("[PayPal] Success: " + e.agentId + " did buy land from " + e.parcelOwnerID +
                            " paying US$ cents " + e.parcelPrice);
                
                land.UpdateLandSold (e.agentId, e.groupId, e.groupOwned, (uint)e.transactionID,
                                     e.parcelPrice, e.parcelArea);
            } else {
                m_log.Error ("[PayPal] Unknown Internal Transaction Type.");
                return;
            }
            // Cleanup.
            lock (m_transactionsInProgress)
                m_transactionsInProgress.Remove (transaction.TxID);
        }

        // Currently hard coded to $0.01 = OS$1
        static decimal ConvertAmountToCurrency (int amount)
        {
            return amount / (decimal)100;
        }

        public Hashtable UserPage (Hashtable request)
        {
            UUID txnID = new UUID ((string)request["txn"]);
            
            if (!m_transactionsInProgress.ContainsKey (txnID)) {
                Hashtable ereply = new Hashtable ();
                
                ereply["int_response_code"] = 404;
                // 200 OK
                ereply["str_response_string"] = "<h1>Invalid Transaction</h1>";
                ereply["content_type"] = "text/html";
                
                return ereply;
            }
            
            PayPalTransaction txn = m_transactionsInProgress[txnID];
            
            string baseUrl = m_scenes[0].RegionInfo.ExternalHostName + ":" + m_scenes[0].RegionInfo.HttpPort;
            
            // Ouch. (This is the PayPal Request URL)
            // TODO: Add in a return page
            // TODO: Add in a cancel page
            string url = "https://" + m_ppurl + "/cgi-bin/webscr?cmd=_xclick" + "&business=" +
                HttpUtility.UrlEncode (txn.SellersEmail) + "&item_name=" + HttpUtility.UrlEncode (txn.Description) +
                    "&item_number=" + HttpUtility.UrlEncode (txn.TxID.ToString ()) + "&amount=" +
                    HttpUtility.UrlEncode (String.Format ("{0:0.00}", ConvertAmountToCurrency (txn.Amount))) +
                    "&page_style=" + HttpUtility.UrlEncode ("Paypal") + "&no_shipping=" +
                    HttpUtility.UrlEncode ("1") + "&return=" + HttpUtility.UrlEncode ("http://" + baseUrl + "/") +
                    "&cancel_return=" + HttpUtility.UrlEncode ("http://" + baseUrl + "/") + "&notify_url=" +
                    HttpUtility.UrlEncode ("http://" + baseUrl + "/ppipn/") + "&no_note=" +
                    HttpUtility.UrlEncode ("1") + "&currency_code=" + HttpUtility.UrlEncode ("USD") + "&lc=" +
                    HttpUtility.UrlEncode ("US") + "&bn=" + HttpUtility.UrlEncode ("PP-BuyNowBF") + "&charset=" +
                    HttpUtility.UrlEncode ("UTF-8") + "";
            
            Dictionary<string, string> replacements = new Dictionary<string, string> ();
            replacements.Add ("{ITEM}",  HttpUtility.HtmlEncode(txn.Description));
            replacements.Add ("{AMOUNT}", HttpUtility.HtmlEncode(String.Format ("{0:0.00}", ConvertAmountToCurrency (txn.Amount))));
            replacements.Add ("{AMOUNTOS}", HttpUtility.HtmlEncode(txn.Amount.ToString ()));
            replacements.Add ("{CURRENCYCODE}", HttpUtility.HtmlEncode("USD"));
            replacements.Add ("{BILLINGLINK}", url);
            replacements.Add ("{OBJECTID}", HttpUtility.HtmlEncode(txn.ObjectID.ToString ()));
            replacements.Add ("{SELLEREMAIL}", HttpUtility.HtmlEncode(txn.SellersEmail));
            
            string template;
            
            try {
                template = File.ReadAllText ("paypal-template.htm");
            } catch (IOException) {
                template = "Error: paypal-template.htm does not exist.";
                m_log.Error ("[PayPal] Unable to load template file.");
            }
            
            foreach (KeyValuePair<string, string> pair in replacements) {
                template = template.Replace (pair.Key, pair.Value);
            }
            
            Hashtable reply = new Hashtable ();
            
            reply["int_response_code"] = 200;
            // 200 OK
            reply["str_response_string"] = template;
            reply["content_type"] = "text/html";
            
            return reply;
        }

        static internal void debugStringDict (Dictionary<string, object> strs)
        {
            foreach (KeyValuePair<string, object> str in strs) {
                m_log.Debug ("[PayPal] '" + str.Key + "' = '" + (string)str.Value + "'");
            }
        }

        public Hashtable IPN (Hashtable request)
        {
            Hashtable reply = new Hashtable ();
            
            // Does not matter what we send back to PP here.
            reply["int_response_code"] = 200;
            // 200 OK
            reply["str_response_string"] = "IPN Processed - Have a nice day.";
            reply["content_type"] = "text/html";
            
            if (!m_active) {
                m_log.Error ("[PayPal] Received IPN request, but module is disabled. Aborting.");
                reply["str_response_string"] = "IPN Not processed. Module is not enabled.";
                return reply;
            }
            
            Dictionary<string, object> postvals = ServerUtils.ParseQueryString ((string)request["body"]);
            string originalPost = (string)request["body"];
            
            string modifiedPost = originalPost + "&cmd=_notify-validate";
            
            HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create ("https://" + m_ppurl +
                                                                               "/cgi-bin/webscr");
            httpWebRequest.Method = "POST";
            
            httpWebRequest.ContentLength = modifiedPost.Length;
            StreamWriter streamWriter = new StreamWriter (httpWebRequest.GetRequestStream ());
            streamWriter.Write (modifiedPost);
            streamWriter.Close ();
            
            string response;
            
            HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse ();
            using (StreamReader streamReader = new StreamReader (httpWebResponse.GetResponseStream ())) {
                response = streamReader.ReadToEnd ();
                streamReader.Close ();
            }
            
            if (httpWebResponse.StatusCode != HttpStatusCode.OK) {
                m_log.Error ("[PayPal] IPN Status code != 200. Aborting.");
                debugStringDict (postvals);
                return reply;
            }
            
            if (!response.Contains ("VERIFIED")) {
                m_log.Error ("[PayPal] IPN was NOT verified. Aborting.");
                debugStringDict (postvals);
                return reply;
            }
            
            // Handle IPN Components
            try {
                if ((string)postvals["payment_status"] != "Completed") {
                    m_log.Warn ("[PayPal] Transaction not confirmed. Aborting.");
                    debugStringDict (postvals);
                    return reply;
                }
                
                if (((string)postvals["mc_currency"]).ToUpper () != "USD") {
                    m_log.Error ("[PayPal] Payment was made in an incorrect currency (" +
                                 postvals["mc_currency"] + "). Aborting.");
                    debugStringDict (postvals);
                    return reply;
                }
                
                // Check we have a transaction with the listed ID.
                UUID txnID = new UUID ((string)postvals["item_number"]);
                PayPalTransaction txn;
                
                lock (m_transactionsInProgress) {
                    if (!m_transactionsInProgress.ContainsKey (txnID)) {
                        m_log.Error ("[PayPal] Recieved IPN request for Payment that is not in progress. Aborting.");
                        debugStringDict (postvals);
                        return reply;
                    }
                    
                    txn = m_transactionsInProgress[txnID];
                }
                
                // Check user paid correctly...
                if (((string)postvals["business"]).ToLower () != txn.SellersEmail.ToLower ()) {
                    m_log.Error ("[PayPal] Expected payment to " + txn.SellersEmail +
                                 " but receiver was " + (string)postvals["business"] + " instead. Aborting.");
                    debugStringDict (postvals);
                    return reply;
                }

                Decimal amountPaid = Decimal.Parse ((string)postvals["mc_gross"]);
                if (System.Math.Abs (ConvertAmountToCurrency (txn.Amount) - amountPaid) > (Decimal)0.001) {
                    m_log.Error ("[PayPal] Expected payment was " + ConvertAmountToCurrency (txn.Amount) +
                                 " but received " + amountPaid + " " + postvals["mc_currency"] + " instead. Aborting.");
                    debugStringDict (postvals);
                    return reply;
                }
                
                // At this point, the user has paid, paid a correct amount, in the correct currency.
                // Time to deliver their items. Do it in a seperate thread, so we can return "OK" to PP.
                Util.FireAndForget (delegate { TransferSuccess (txn); });
            } catch (KeyNotFoundException) {
                m_log.Error ("[PayPal] Received badly formatted IPN notice. Aborting.");
                debugStringDict (postvals);
                return reply;
            }
            // Wheeeee
            
            return reply;
        }

        #endregion


        #region Implementation of IRegionModuleBase

        public string Name {
            get { return "PayPalMoneyModule"; }
        }

        public Type ReplaceableInterface {
            get { return null; }
        }

        public void Initialise (IConfigSource source)
        {
            m_log.Info ("[PayPal] Initialising.");
            m_config = source;

            IConfig config = m_config.Configs["PayPal"];
            
            if (null == config) {
                m_log.Warn ("[PayPal] No configuration specified. Skipping.");
                return;
            }
            
            if (!config.GetBoolean ("Enabled", false))
            {
                m_log.Info ("[PayPal] Not enabled. (to enable set \"Enabled = true\" in [PayPal])");
                return;
            }

            m_ppurl = config.GetString ("PayPalURL", m_ppurl);

            m_allowGridEmails = config.GetBoolean ("AllowGridEmails", false);
            m_allowGroups = config.GetBoolean ("AllowGroups", false);
            m_balanceOnEntry = config.GetBoolean ("BalanceOnEntry", true);
            m_messageOnEntry = config.GetString ("MessageOnEntry", m_messageOnEntry);
            m_messageDelayAtLogin = config.GetInt("MessageDelayAtLogin", m_messageDelayAtLogin);
            
            IConfig startupConfig = m_config.Configs["Startup"];

            if (startupConfig != null)
            {
                m_enabled = (startupConfig.GetString("economymodule", "PayPalMoneyModule") == "PayPalMoneyModule");

                if (!m_enabled) {
                    m_log.Info ("[PayPal] Not enabled. (to enable set \"economymodule = PayPalMoneyModule\" in [Startup])");
                    return;
                }
            }

            IConfig economyConfig = m_config.Configs["Economy"];

            if (economyConfig != null)
            {
                PriceEnergyUnit = economyConfig.GetInt("PriceEnergyUnit", 100);
                PriceObjectClaim = economyConfig.GetInt("PriceObjectClaim", 10);
                PricePublicObjectDecay = economyConfig.GetInt("PricePublicObjectDecay", 4);
                PricePublicObjectDelete = economyConfig.GetInt("PricePublicObjectDelete", 4);
                PriceParcelClaim = economyConfig.GetInt("PriceParcelClaim", 1);
                PriceParcelClaimFactor = economyConfig.GetFloat("PriceParcelClaimFactor", 1f);
                PriceUpload = economyConfig.GetInt("PriceUpload", 0);
                PriceRentLight = economyConfig.GetInt("PriceRentLight", 5);
                TeleportMinPrice = economyConfig.GetInt("TeleportMinPrice", 2);
                TeleportPriceExponent = economyConfig.GetFloat("TeleportPriceExponent", 2f);
                EnergyEfficiency = economyConfig.GetFloat("EnergyEfficiency", 1);
                PriceObjectRent = economyConfig.GetFloat("PriceObjectRent", 1);
                PriceObjectScaleFactor = economyConfig.GetFloat("PriceObjectScaleFactor", 10);
                PriceParcelRent = economyConfig.GetInt("PriceParcelRent", 1);
                PriceGroupCreate = economyConfig.GetInt("PriceGroupCreate", -1);
            }

            m_log.Info ("[PayPal] Loaded.");
            
            m_enabled = true;
        }

        public void PostInitialise ()
        {

        }

        public void Close ()
        {
            m_active = false;
        }

        public void AddRegion (Scene scene)
        {
            lock (m_scenes)
                m_scenes.Add (scene);
            
            if (m_enabled) {
                m_log.Info ("[PayPal] Found Scene.");

                scene.RegisterModuleInterface<IMoneyModule> (this);
                IHttpServer httpServer = MainServer.Instance;
                
                lock (m_scenel)
                {
                    if (m_scenel.Count == 0)
                    {
                        // XMLRPCHandler = scene;
                        
                        // To use the following you need to add:
                        // -helperuri <ADDRESS TO HERE OR grid MONEY SERVER>
                        // to the command line parameters you use to start up your client
                        // This commonly looks like -helperuri http://127.0.0.1:9000/
                        
                        // Local Server..  enables functionality only.
                        httpServer.AddXmlRPCHandler("getCurrencyQuote", quote_func);
                        httpServer.AddXmlRPCHandler("buyCurrency", buy_func);
                        httpServer.AddXmlRPCHandler ("preflightBuyLandPrep", preflightBuyLandPrep_func);
                        httpServer.AddXmlRPCHandler ("buyLandPrep", landBuy_func);
                    }
                    
                    if (m_scenel.ContainsKey (scene.RegionInfo.RegionHandle))
                    {
                        m_scenel[scene.RegionInfo.RegionHandle] = scene;
                    }
                    else
                    {
                        m_scenel.Add (scene.RegionInfo.RegionHandle, scene);
                    }
                }
                
                scene.EventManager.OnNewClient += OnNewClient;
                scene.EventManager.OnMakeRootAgent += MakeRootAgent;
                scene.EventManager.OnMoneyTransfer += OnMoneyTransfer;
                scene.EventManager.OnValidateLandBuy += ValidateLandBuy;
                scene.EventManager.OnLandBuy += processLandBuy;
            }
        }

        #region Basic Plumbing of Currency Events

        void OnNewClient (IClientAPI client)
        {
            // Subscribe to Money messages
            client.OnEconomyDataRequest += EconomyDataRequestHandler;
            client.OnMoneyBalanceRequest += OnMoneyBalanceRequest;
            client.OnRequestPayPrice += requestPayPrice;
            client.OnObjectBuy += ObjectBuy;
            client.OnRetrieveInstantMessages += RetrieveInstantMessages;
        }

        /// <summary>
        /// Event Handler for when a root agent becomes a root agent
        /// </summary>
        /// <param name="avatar"></param>
        private void MakeRootAgent(ScenePresence avatar)
        {
            IClientAPI client = avatar.ControllingClient;

            if (m_balanceOnEntry)
            {
                client.SendMoneyBalance(UUID.Random(), true, new byte[0], m_maxBalance, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);

                if (m_messageOnEntry != "")
                    SendEntryMessage(client);
            }
        }

        private void RetrieveInstantMessages(IClientAPI client)
        {
            // m_log.DebugFormat("[PayPal]: RetrieveInstantMessages {0}", client.AgentId);

            // Show warning message
            if (m_messageOnEntry != "")
            {
                Util.FireAndForget(delegate
                {
                    Thread.Sleep(m_messageDelayAtLogin);
                    SendEntryMessage(client);
                });
            }
        }

        /// <summary>
        /// Locates a IClientAPI for the client specified
        /// </summary>
        /// <param name="AgentID"></param>
        /// <returns></returns>
        private IClientAPI LocateClientObject(UUID AgentID)
        {
            ScenePresence tPresence = null;
            IClientAPI rclient = null;

            lock (m_scenel)
            {
                foreach (Scene _scene in m_scenel.Values)
                {
                    tPresence = _scene.GetScenePresence(AgentID);
                    if (tPresence != null)
                    {
                        if (!tPresence.IsChildAgent)
                        {
                            rclient = tPresence.ControllingClient;
                        }
                    }
                    if (rclient != null)
                    {
                        return rclient;
                    }
                }
            }
            return null;
        }

        internal Scene LocateSceneClientIn (UUID agentID)
        {
            ScenePresence avatar = null;
            
            foreach (Scene scene in m_scenes)
            {
                if (scene.TryGetScenePresence (agentID, out avatar))
                {
                    if (!avatar.IsChildAgent)
                    {
                        return avatar.Scene;
                    }
                }
            }
            
            return null;
        }

        public void ObjectBuy (IClientAPI remoteClient, UUID agentID, UUID sessionID, UUID groupID,
                               UUID categoryID, uint localID, byte saleType, int salePrice)
        {
            if (!m_active)
                return;
            
            IClientAPI user = null;
            Scene scene = null;
            
            // Find the user's controlling client.
            lock (m_scenes) {
                foreach (Scene sc in m_scenes) {
                    ScenePresence av = sc.GetScenePresence (agentID);
                    
                    if ((av != null) && (av.IsChildAgent == false)) {
                        // Found the client,
                        // and their root scene.
                        user = av.ControllingClient;
                        scene = sc;
                    }
                }
            }
            
            if (scene == null || user == null) {
                m_log.Warn ("[PayPal] Unable to find scene or user! Aborting transaction.");
                return;
            }
            
            if (salePrice == 0) {
                IBuySellModule module = scene.RequestModuleInterface<IBuySellModule> ();
                if (module == null) {
                    m_log.Error ("[PayPal] Missing BuySellModule! Transaction failed.");
                    return;
                }
                module.BuyObject (remoteClient, categoryID, localID, saleType, salePrice);
                return;
            }
            
            SceneObjectPart sop = scene.GetSceneObjectPart (localID);
            if (sop == null) {
                m_log.Error ("[PayPal] Unable to find SceneObjectPart that was paid. Aborting transaction.");
                return;
            }
            
            string email;
            
            if (sop.OwnerID == sop.GroupID) {
                if (m_allowGroups) {
                    if (!GetEmail (scene.RegionInfo.ScopeID, sop.OwnerID, out email)) {
                        m_log.Warn ("[PayPal] Unknown email address of group " + sop.OwnerID);
                        return;
                    }
                } else {
                    m_log.Warn ("[PayPal] Purchase of group owned objects is disabled.");
                    return;
                }
            } else {
                if (!GetEmail (scene.RegionInfo.ScopeID, sop.OwnerID, out email)) {
                    m_log.Warn ("[PayPal] Unknown email address of user " + sop.OwnerID);
                    return;
                }
            }
            
            m_log.Info ("[PayPal] Start: " + agentID + " wants to buy object " + sop.UUID + " from " + sop.OwnerID +
                        " with email " + email + " costing US$ cents " + salePrice);
            
            PayPalTransaction txn = new PayPalTransaction (agentID, sop.OwnerID, email, salePrice, scene, sop.UUID,
                                                           "Item Purchase - " + sop.Name + " (" + saleType + ")",
                                                           PayPalTransaction.InternalTransactionType.Purchase,
                                                           categoryID, saleType);
            
            // Add transaction to queue
            lock (m_transactionsInProgress)
                m_transactionsInProgress.Add (txn.TxID, txn);
            
            string baseUrl = m_scenes[0].RegionInfo.ExternalHostName + ":" + m_scenes[0].RegionInfo.HttpPort;

            user.SendLoadURL ("PayPal", txn.ObjectID, txn.To, false, "Confirm purchase?", "http://" +
                              baseUrl + "/pp/?txn=" + txn.TxID);
        }

        public void MoveMoney(UUID fromAgentID, UUID toAgentID, int amount, string text)
        {
        }

        public bool MoveMoney(UUID fromUser, UUID toUser, int amount, MoneyTransactionType type, string text)
        {
            return true;
        }

        public void requestPayPrice (IClientAPI client, UUID objectID)
        {
            Scene scene = (Scene)client.Scene;
            if (scene == null)
                return;
            
            SceneObjectPart task = scene.GetSceneObjectPart (objectID);
            if (task == null)
                return;
            SceneObjectGroup @group = task.ParentGroup;
            SceneObjectPart root = @group.RootPart;
            
            client.SendPayPrice (objectID, root.PayPrice);
        }

        /// <summary>
        /// Event called Economy Data Request handler.
        /// </summary>
        /// <param name="user"></param>
        public void EconomyDataRequestHandler(IClientAPI user)
        {
            Scene s = (Scene)user.Scene;

            user.SendEconomyData(EnergyEfficiency, s.RegionInfo.ObjectCapacity, ObjectCount, PriceEnergyUnit, PriceGroupCreate,
                                 PriceObjectClaim, PriceObjectRent, PriceObjectScaleFactor, PriceParcelClaim, PriceParcelClaimFactor,
                                 PriceParcelRent, PricePublicObjectDecay, PricePublicObjectDelete, PriceRentLight, PriceUpload,
                                 TeleportMinPrice, TeleportPriceExponent);
        }

        void OnMoneyBalanceRequest (IClientAPI client, UUID agentID, UUID SessionID, UUID TransactionID)
        {
            if (client.AgentId == agentID && client.SessionId == SessionID && (client == LocateClientObject(agentID)))
            {
                client.SendMoneyBalance (TransactionID, true, new byte[0], m_maxBalance, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
            }
        }

        private void ValidateLandBuy (Object osender, EventManager.LandBuyArgs e)
        {
            // confirm purchase of land for free
            if (e.parcelPrice == 0) {
                lock (e) {
                    e.economyValidated = true;
                }
            }
        }

        private void processLandBuy (Object osender, EventManager.LandBuyArgs e)
        {
            if (!m_active)
                return;
            
            if (e.parcelPrice == 0)
                return;
            
            IClientAPI user = null;
            Scene scene = null;
            
            // Find the user's controlling client.
            lock (m_scenes) {
                foreach (Scene sc in m_scenes) {
                    ScenePresence av = sc.GetScenePresence (e.agentId);
                    
                    if ((av != null) && (av.IsChildAgent == false)) {
                        // Found the client,
                        // and their root scene.
                        user = av.ControllingClient;
                        scene = sc;
                    }
                }
            }
            
            if (scene == null || user == null) {
                m_log.Error ("[PayPal] Unable to find scene or user! Aborting transaction.");
                return;
            }
            
            string email;
            
            if ((e.parcelOwnerID == e.groupId) || e.groupOwned) {
                if (m_allowGroups) {
                    if (!GetEmail (scene.RegionInfo.ScopeID, e.parcelOwnerID, out email)) {
                        m_log.Warn ("[PayPal] Unknown email address of group " + e.parcelOwnerID);
                        return;
                    }
                } else {
                    m_log.Warn ("[PayPal] Purchases of group owned land is disabled.");
                    return;
                }
            } else {
                if (!GetEmail (scene.RegionInfo.ScopeID, e.parcelOwnerID, out email)) {
                    m_log.Warn ("[PayPal] Unknown email address of user " + e.parcelOwnerID);
                    return;
                }
            }
            
            m_log.Info ("[PayPal] Start: " + e.agentId + " wants to buy land from " + e.parcelOwnerID +
                        " with email " + email + " costing US$ cents " + e.parcelPrice);
            
            PayPalTransaction txn;
            txn = new PayPalTransaction (e.agentId, e.parcelOwnerID, email, e.parcelPrice, scene,
                                         "Buy Land", PayPalTransaction.InternalTransactionType.Land, e);
            
            // Add transaction to queue
            lock (m_transactionsInProgress)
                m_transactionsInProgress.Add (txn.TxID, txn);
            
            string baseUrl = m_scenes[0].RegionInfo.ExternalHostName + ":" + m_scenes[0].RegionInfo.HttpPort;
            
            user.SendLoadURL ("PayPal", txn.ObjectID, txn.To, false, "Confirm payment?", "http://" +
                              baseUrl + "/pp/?txn=" + txn.TxID);
        }

        public XmlRpcResponse preflightBuyLandPrep_func (XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse ret = new XmlRpcResponse ();
            Hashtable retparam = new Hashtable ();
            Hashtable membershiplevels = new Hashtable ();
            ArrayList levels = new ArrayList ();
            Hashtable level = new Hashtable ();
            level.Add ("id", "00000000-0000-0000-0000-000000000000");
            level.Add ("description", "some level");
            levels.Add (level);
            //membershiplevels.Add("levels",levels);
            
            Hashtable landuse = new Hashtable ();
            landuse.Add ("upgrade", false);
            landuse.Add ("action", "http://invaliddomaininvalid.com/");
            
            Hashtable currency = new Hashtable ();
            currency.Add ("estimatedCost", 0);
            
            Hashtable membership = new Hashtable ();
            membershiplevels.Add ("upgrade", false);
            membershiplevels.Add ("action", "http://invaliddomaininvalid.com/");
            membershiplevels.Add ("levels", membershiplevels);
            
            retparam.Add ("success", true);
            retparam.Add ("currency", currency);
            retparam.Add ("membership", membership);
            retparam.Add ("landuse", landuse);
            retparam.Add ("confirm", "asdfajsdkfjasdkfjalsdfjasdf");
            
            ret.Value = retparam;
            
            return ret;
        }

        public XmlRpcResponse landBuy_func (XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse ret = new XmlRpcResponse ();
            Hashtable retparam = new Hashtable ();
            
            retparam.Add ("success", true);
            ret.Value = retparam;
            
            return ret;
        }

        private bool GetEmail (UUID scope, UUID key, out string email)
        {
            if (m_usersemail.TryGetValue (key, out email))
                return !string.IsNullOrEmpty (email);
            
            if (!m_allowGridEmails)
                return false;
            
            m_log.Info ("[PayPal] Fetching email address from grid for " + key);
            
            IUserAccountService userAccountService = m_scenes[0].UserAccountService;
            UserAccount ua;
            
            ua = userAccountService.GetUserAccount (scope, key);
            
            if (ua == null)
                return false;
            
            if (string.IsNullOrEmpty (ua.Email))
                return false;
            
            // return email address found and cache it
            email = ua.Email;
            m_usersemail[ua.PrincipalID] = email;
            return true;
        }

        private void SendInstantMessage (UUID dest, string message)
        {
            IClientAPI user = null;
            
            // Find the user's controlling client.
            lock (m_scenes) {
                foreach (Scene sc in m_scenes) {
                    ScenePresence av = sc.GetScenePresence (dest);
                    
                    if ((av != null) && (av.IsChildAgent == false)) {
                        // Found the client,
                        // and their root scene.
                        user = av.ControllingClient;
                    }
                }
            }
            
            if (user == null)
                return;
            
            UUID transaction = UUID.Random ();
            
            GridInstantMessage msg = new GridInstantMessage ();
            msg.fromAgentID = new Guid (UUID.Zero.ToString ());
            // From server
            msg.toAgentID = new Guid (dest.ToString ());
            msg.imSessionID = new Guid (transaction.ToString ());
            msg.timestamp = (uint)Util.UnixTimeSinceEpoch ();
            msg.fromAgentName = "PayPal";
            msg.dialog = (byte)19;
            // Object msg
            msg.fromGroup = false;
            msg.offline = (byte)0;
            msg.ParentEstateID = (uint)0;
            msg.Position = Vector3.Zero;
            msg.RegionID = new Guid (UUID.Zero.ToString ());
            msg.binaryBucket = new byte[0];
            msg.message = message;
            
            user.SendInstantMessage (msg);
        }

        private void SendEntryMessage(IClientAPI client)
        {
            GridInstantMessage msg = new GridInstantMessage();
            msg.imSessionID = UUID.Zero.Guid;
            msg.fromAgentID = UUID.Zero.Guid;
            msg.toAgentID = client.AgentId.Guid;
            msg.timestamp = (uint)Util.UnixTimeSinceEpoch();
            msg.fromAgentName = "System";
            msg.message = m_messageOnEntry;
            msg.dialog = (byte)OpenMetaverse.InstantMessageDialog.ConsoleAndChatHistory;
            msg.fromGroup = false;
            msg.offline = (byte)0;
            msg.ParentEstateID = 0;
            msg.Position = Vector3.Zero;
            msg.RegionID = UUID.Zero.Guid;
            msg.binaryBucket = new byte[0];

            client.SendInstantMessage(msg);
        }

        #endregion

        public void RemoveRegion (Scene scene)
        {
            lock (m_scenes)
                m_scenes.Remove (scene);
            
            if (m_enabled)
            {
                scene.EventManager.OnNewClient -= OnNewClient;
                scene.EventManager.OnMakeRootAgent -= MakeRootAgent;
                scene.EventManager.OnMoneyTransfer -= OnMoneyTransfer;
                scene.EventManager.OnValidateLandBuy -= ValidateLandBuy;
                scene.EventManager.OnLandBuy -= processLandBuy;
            }
        }

        public void RegionLoaded (Scene scene)
        {
            if (m_enabled)
            {
                lock (m_setupLock)
                    if (m_setup == false) {
                        m_setup = true;
                        FirstRegionLoaded ();
                    }
            }
        }

        public void FirstRegionLoaded ()
        {
            m_log.Info ("[PayPal] Loading predefined users and groups.");

            // Users
            IConfig users = m_config.Configs["PayPal Users"];
            
            if (null == users) {
                m_log.Warn ("[PayPal] No users specified in local ini file.");
            } else {
                IUserAccountService userAccountService = m_scenes[0].UserAccountService;
                
                // This aborts at the slightest provocation
                // We realise this may be inconvenient for you,
                // however it is important when dealing with
                // financial matters to error check everything.
                
                foreach (string user in users.GetKeys ()) {
                    UUID tmp;
                    if (UUID.TryParse (user, out tmp)) {
                        m_log.Debug ("[PayPal] User is UUID, skipping lookup...");
                        string email = users.GetString (user);
                        m_usersemail[tmp] = email;
                        continue;
                    }
                    
                    m_log.Debug ("[PayPal] Looking up UUID for user " + user);
                    string[] username = user.Split (new[] { ' ' }, 2);
                    UserAccount ua = userAccountService.GetUserAccount (UUID.Zero, username[0], username[1]);
                    
                    if (ua != null) {
                        m_log.Debug ("[PayPal] Found user, " + user + " = " + ua.PrincipalID);
                        string email = users.GetString (user);
                        
                        if (string.IsNullOrEmpty (email)) {
                            m_log.Error ("[PayPal] PayPal email address not set for user " + user +
                                         " in [PayPal Users] config section. Skipping.");
                            m_usersemail[ua.PrincipalID] = "";
                        } else {
                            if (!PayPalHelpers.IsValidEmail (email)) {
                                m_log.Error ("[PayPal] PayPal email address not valid for user " + user +
                                             " in [PayPal Users] config section. Skipping.");
                                m_usersemail[ua.PrincipalID] = "";
                            } else {
                                m_usersemail[ua.PrincipalID] = email;
                            }
                        }
                    // UserProfileData was null
                    } else {
                        m_log.Error ("[PayPal] Error, User Profile not found for user " + user +
                                     ". Check the spelling and/or any associated grid services.");
                    }
                }
            }
            
            // Groups
            IConfig groups = m_config.Configs["PayPal Groups"];
            
            if (!m_allowGroups || null == groups) {
                m_log.Warn ("[PayPal] Groups disabled or no groups specified in local ini file.");
            } else {
                // This aborts at the slightest provocation
                // We realise this may be inconvenient for you,
                // however it is important when dealing with
                // financial matters to error check everything.
                
                foreach (string @group in groups.GetKeys ()) {
                    m_log.Debug ("[PayPal] Defining email address for UUID for group " + @group);
                    UUID groupID = new UUID (@group);
                    string email = groups.GetString (@group);
                    
                    if (string.IsNullOrEmpty (email)) {
                        m_log.Error ("[PayPal] PayPal email address not set for group " +
                                     @group + " in [PayPal Groups] config section. Skipping.");
                        m_usersemail[groupID] = "";
                    } else {
                        if (!PayPalHelpers.IsValidEmail (email)) {
                            m_log.Error ("[PayPal] PayPal email address not valid for group " +
                                         @group + " in [PayPal Groups] config section. Skipping.");
                            m_usersemail[groupID] = "";
                        } else {
                            m_usersemail[groupID] = email;
                        }
                    }
                }
            }
            
            // Add HTTP Handlers (user, then PP-IPN)
            MainServer.Instance.AddHTTPHandler ("/pp/", UserPage);
            MainServer.Instance.AddHTTPHandler ("/ppipn/", IPN);
            
            // XMLRPC Handlers for Standalone
            MainServer.Instance.AddXmlRPCHandler ("getCurrencyQuote", quote_func);
            MainServer.Instance.AddXmlRPCHandler ("buyCurrency", buy_func);
            
            m_active = true;
        }

        #endregion

        #region Implementation of IMoneyModule

        public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount, UUID txn, out string result)
        {
            result = "";
            return false;
            // Objects cant give PP Money. (in theory it's doable however, if the user is in the sim.)
        }

        // This will be the maximum amount the user
        // is able to spend due to client limitations.
        // It is set to the equivilent of US$10K
        // as this is PayPal's maximum transaction
        // size.
        //
        // This is 1 Million cents.
        public int GetBalance (UUID agentID)
        {
            return m_maxBalance;
        }

        public bool UploadCovered (UUID agentID, int amount)
        {
            return true;
        }

        public bool AmountCovered (UUID agentID, int amount)
        {
            return true;
        }

        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string extraData)
        {
            // N/A
        }

        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type)
        {
            // N/A
        }

        public void ApplyUploadCharge (UUID agentID, int amount, string text)
        {
            // N/A
        }

        public int UploadCharge {
            get { return 0; }
        }

        public int GroupCreationCharge {
            get { return 0; }
        }

        public event ObjectPaid OnObjectPaid;

        #endregion

        #region Some Quick Funcs needed for the client

        public XmlRpcResponse quote_func (XmlRpcRequest request, IPEndPoint remoteClient)
        {
            // Hashtable requestData = (Hashtable) request.Params[0];
            // UUID agentId = UUID.Zero;
            Hashtable quoteResponse = new Hashtable ();
            XmlRpcResponse returnval = new XmlRpcResponse ();
            
            
            Hashtable currencyResponse = new Hashtable ();
            currencyResponse.Add ("estimatedCost", 0);
            currencyResponse.Add ("currencyBuy", m_maxBalance);
            
            quoteResponse.Add ("success", true);
            quoteResponse.Add ("currency", currencyResponse);
            quoteResponse.Add ("confirm", "asdfad9fj39ma9fj");
            
            returnval.Value = quoteResponse;
            return returnval;
        }

        public XmlRpcResponse buy_func (XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse returnval = new XmlRpcResponse ();
            Hashtable returnresp = new Hashtable ();
            returnresp.Add ("success", true);
            returnval.Value = returnresp;
            return returnval;
        }
        
        #endregion
        
    }
}
