﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2016 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.JSON;

namespace ArchiSteamFarm {
	internal sealed class Trading : IDisposable {
		private sealed class ParseTradeResult {
			internal enum EResult : byte {
				Unknown,
				AcceptedWithItemLose,
				AcceptedWithoutItemLose,
				RejectedTemporarily,
				RejectedPermanently
			}

			internal readonly ulong TradeID;
			internal readonly EResult Result;

			internal ParseTradeResult(ulong tradeID, EResult result) {
				if ((tradeID == 0) || (result == EResult.Unknown)) {
					throw new ArgumentNullException(nameof(tradeID) + " || " + nameof(result));
				}

				TradeID = tradeID;
				Result = result;
			}
		}

		internal const byte MaxItemsPerTrade = 150; // This is due to limit on POST size in WebBrowser
		internal const byte MaxTradesPerAccount = 5; // This is limit introduced by Valve

		private static readonly SemaphoreSlim InventorySemaphore = new SemaphoreSlim(1);

		private readonly Bot Bot;
		private readonly ConcurrentHashSet<ulong> IgnoredTrades = new ConcurrentHashSet<ulong>();
		private readonly SemaphoreSlim TradesSemaphore = new SemaphoreSlim(1);

		private byte ParsingTasks;

		internal static async Task LimitInventoryRequestsAsync() {
			await InventorySemaphore.WaitAsync().ConfigureAwait(false);
			Task.Run(async () => {
				await Task.Delay(Program.GlobalConfig.InventoryLimiterDelay * 1000).ConfigureAwait(false);
				InventorySemaphore.Release();
			}).Forget();
		}

		internal Trading(Bot bot) {
			if (bot == null) {
				throw new ArgumentNullException(nameof(bot));
			}

			Bot = bot;
		}

		public void Dispose() {
			IgnoredTrades.Dispose();
			TradesSemaphore.Dispose();
		}

		internal void OnDisconnected() => IgnoredTrades.ClearAndTrim();

		internal async Task CheckTrades() {
			lock (TradesSemaphore) {
				if (ParsingTasks >= 2) {
					return;
				}

				ParsingTasks++;
			}

			await TradesSemaphore.WaitAsync().ConfigureAwait(false);

			await ParseActiveTrades().ConfigureAwait(false);
			lock (TradesSemaphore) {
				ParsingTasks--;
			}

			TradesSemaphore.Release();
		}

		private async Task ParseActiveTrades() {
			if (string.IsNullOrEmpty(Bot.BotConfig.SteamApiKey)) {
				return;
			}

			HashSet<Steam.TradeOffer> tradeOffers = Bot.ArchiWebHandler.GetActiveTradeOffers();
			if ((tradeOffers == null) || (tradeOffers.Count == 0)) {
				return;
			}

			if (tradeOffers.RemoveWhere(tradeoffer => IgnoredTrades.Contains(tradeoffer.TradeOfferID)) > 0) {
				if (tradeOffers.Count == 0) {
					return;
				}
			}

			ParseTradeResult[] results = await Task.WhenAll(tradeOffers.Select(ParseTrade)).ConfigureAwait(false);

			if (Bot.HasMobileAuthenticator) {
				HashSet<ulong> acceptedWithItemLoseTradeIDs = new HashSet<ulong>(results.Where(result => (result != null) && (result.Result == ParseTradeResult.EResult.AcceptedWithItemLose)).Select(result => result.TradeID));
				if (acceptedWithItemLoseTradeIDs.Count > 0) {
					await Task.Delay(1000).ConfigureAwait(false); // Sometimes we can be too fast for Steam servers to generate confirmations, wait a short moment
					await Bot.AcceptConfirmations(true, Steam.ConfirmationDetails.EType.Trade, 0, acceptedWithItemLoseTradeIDs).ConfigureAwait(false);
				}
			}

			if (results.Any(result => (result != null) && ((result.Result == ParseTradeResult.EResult.AcceptedWithItemLose) || (result.Result == ParseTradeResult.EResult.AcceptedWithoutItemLose)))) {
				// If we finished a trade, perform a loot if user wants to do so
				await Bot.LootIfNeeded().ConfigureAwait(false);
			}
		}

		private async Task<ParseTradeResult> ParseTrade(Steam.TradeOffer tradeOffer) {
			if (tradeOffer == null) {
				Logging.LogNullError(nameof(tradeOffer), Bot.BotName);
				return null;
			}

			if (tradeOffer.State != Steam.TradeOffer.ETradeOfferState.Active) {
				Logging.LogGenericError("Ignoring trade in non-active state!", Bot.BotName);
				return null;
			}

			ParseTradeResult result = await ShouldAcceptTrade(tradeOffer).ConfigureAwait(false);
			if (result == null) {
				Logging.LogNullError(nameof(result), Bot.BotName);
				return null;
			}

			switch (result.Result) {
				case ParseTradeResult.EResult.AcceptedWithItemLose:
				case ParseTradeResult.EResult.AcceptedWithoutItemLose:
					Logging.LogGenericInfo("Accepting trade: " + tradeOffer.TradeOfferID, Bot.BotName);
					await Bot.ArchiWebHandler.AcceptTradeOffer(tradeOffer.TradeOfferID).ConfigureAwait(false);
					break;
				case ParseTradeResult.EResult.RejectedPermanently:
				case ParseTradeResult.EResult.RejectedTemporarily:
					if (result.Result == ParseTradeResult.EResult.RejectedPermanently) {
						if (Bot.BotConfig.IsBotAccount) {
							Logging.LogGenericInfo("Rejecting trade: " + tradeOffer.TradeOfferID, Bot.BotName);
							Bot.ArchiWebHandler.DeclineTradeOffer(tradeOffer.TradeOfferID);
							break;
						}

						IgnoredTrades.Add(tradeOffer.TradeOfferID);
					}

					Logging.LogGenericInfo("Ignoring trade: " + tradeOffer.TradeOfferID, Bot.BotName);
					break;
			}

			return result;
		}

		private async Task<ParseTradeResult> ShouldAcceptTrade(Steam.TradeOffer tradeOffer) {
			if (tradeOffer == null) {
				Logging.LogNullError(nameof(tradeOffer), Bot.BotName);
				return null;
			}

			// Always accept trades when we're not losing anything
			if (tradeOffer.ItemsToGive.Count == 0) {
				// Unless it's steam fuckup and we're dealing with broken trade
				return tradeOffer.ItemsToReceive.Count > 0 ? new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.AcceptedWithoutItemLose) : new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.RejectedTemporarily);
			}

			// Always accept trades from SteamMasterID
			if ((tradeOffer.OtherSteamID64 != 0) && (tradeOffer.OtherSteamID64 == Bot.BotConfig.SteamMasterID)) {
				return new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.AcceptedWithItemLose);
			}

			// If we don't have SteamTradeMatcher enabled, this is the end for us
			if (!Bot.BotConfig.SteamTradeMatcher) {
				return new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.RejectedPermanently);
			}

			// Decline trade if we're giving more count-wise
			if (tradeOffer.ItemsToGive.Count > tradeOffer.ItemsToReceive.Count) {
				return new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.RejectedPermanently);
			}

			// Decline trade if we're losing anything but steam cards, or if it's non-dupes trade
			if (!tradeOffer.IsSteamCardsRequest() || !tradeOffer.IsFairTypesExchange()) {
				return new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.RejectedPermanently);
			}

			// At this point we're sure that STM trade is valid

			// Fetch trade hold duration
			byte? holdDuration = await Bot.ArchiWebHandler.GetTradeHoldDuration(tradeOffer.TradeOfferID).ConfigureAwait(false);
			if (!holdDuration.HasValue) {
				// If we can't get trade hold duration, reject trade temporarily
				return new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.RejectedTemporarily);
			}

			// If user has a trade hold, we add extra logic
			if (holdDuration.Value > 0) {
				// If trade hold duration exceeds our max, or user asks for cards with short lifespan, reject the trade
				if ((holdDuration.Value > Program.GlobalConfig.MaxTradeHoldDuration) || tradeOffer.ItemsToGive.Any(item => GlobalConfig.GlobalBlacklist.Contains(item.RealAppID))) {
					return new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.RejectedPermanently);
				}
			}

			// Now check if it's worth for us to do the trade
			await LimitInventoryRequestsAsync().ConfigureAwait(false);

			HashSet<Steam.Item> inventory = await Bot.ArchiWebHandler.GetMySteamInventory(false).ConfigureAwait(false);
			if ((inventory == null) || (inventory.Count == 0)) {
				return new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.AcceptedWithItemLose); // OK, assume that this trade is valid, we can't check our EQ
			}

			// Get appIDs we're interested in
			HashSet<uint> appIDs = new HashSet<uint>(tradeOffer.ItemsToGive.Select(item => item.RealAppID));

			// Now remove from our inventory all items we're NOT interested in
			inventory.RemoveWhere(item => !appIDs.Contains(item.RealAppID));

			// If for some reason Valve is talking crap and we can't find mentioned items, assume OK
			if (inventory.Count == 0) {
				return new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.AcceptedWithItemLose);
			}

			// Now let's create a map which maps items to their amount in our EQ
			Dictionary<ulong, uint> amountMap = new Dictionary<ulong, uint>();
			foreach (Steam.Item item in inventory) {
				uint amount;
				if (amountMap.TryGetValue(item.ClassID, out amount)) {
					amountMap[item.ClassID] = amount + item.Amount;
				} else {
					amountMap[item.ClassID] = item.Amount;
				}
			}

			// Calculate our value of items to give
			List<uint> amountsToGive = new List<uint>(tradeOffer.ItemsToGive.Count);
			Dictionary<ulong, uint> amountMapToGive = new Dictionary<ulong, uint>(amountMap);
			foreach (ulong key in tradeOffer.ItemsToGive.Select(item => item.ClassID)) {
				uint amount;
				if (!amountMapToGive.TryGetValue(key, out amount)) {
					amountsToGive.Add(0);
					continue;
				}

				amountsToGive.Add(amount);
				amountMapToGive[key] = amount - 1; // We're giving one, so we have one less
			}

			// Sort it ascending
			amountsToGive.Sort();

			// Calculate our value of items to receive
			List<uint> amountsToReceive = new List<uint>(tradeOffer.ItemsToReceive.Count);
			Dictionary<ulong, uint> amountMapToReceive = new Dictionary<ulong, uint>(amountMap);
			foreach (ulong key in tradeOffer.ItemsToReceive.Select(item => item.ClassID)) {
				uint amount;
				if (!amountMapToReceive.TryGetValue(key, out amount)) {
					amountsToReceive.Add(0);
					continue;
				}

				amountsToReceive.Add(amount);
				amountMapToReceive[key] = amount + 1; // We're getting one, so we have one more
			}

			// Sort it ascending
			amountsToReceive.Sort();

			// Check actual difference
			// We sum only values at proper indexes of giving, because user might be overpaying
			int difference = amountsToGive.Select((t, i) => (int) (t - amountsToReceive[i])).Sum();

			// Trade is worth for us if the difference is greater than 0
			return difference > 0 ? new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.AcceptedWithItemLose) : new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.RejectedTemporarily);
		}
	}
}
