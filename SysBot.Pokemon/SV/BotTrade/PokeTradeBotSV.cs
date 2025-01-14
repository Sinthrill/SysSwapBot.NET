﻿using PKHeX.Core;
using PKHeX.Core.Searching;
using SysBot.Base;
using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.PokeDataOffsetsSV;
using System.Collections.Generic;
using System.IO;
using PKHeX.Core.AutoMod;
using SysBot.Pokemon.TradeHub;

namespace SysBot.Pokemon
{
    // ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
    public class PokeTradeBotSV : PokeRoutineExecutor9SV, ICountBot
    {
        private readonly PokeTradeHub<PK9> Hub;
        private readonly TradeSettings TradeSettings;
        public readonly TradeAbuseSettings AbuseSettings;

        public ICountSettings Counts => TradeSettings;

        private static readonly Random rnd = new();

        /// <summary>
        /// Folder to dump received trade data to.
        /// </summary>
        /// <remarks>If null, will skip dumping.</remarks>
        private readonly IDumper DumpSetting;

        /// <summary>
        /// Synchronized start for multiple bots.
        /// </summary>
        public bool ShouldWaitAtBarrier { get; private set; }

        /// <summary>
        /// Tracks failed synchronized starts to attempt to re-sync.
        /// </summary>
        public int FailedBarrier { get; private set; }

        public PokeTradeBotSV(PokeTradeHub<PK9> hub, PokeBotState cfg) : base(cfg)
        {
            Hub = hub;
            TradeSettings = hub.Config.Trade;
            AbuseSettings = hub.Config.TradeAbuse;
            DumpSetting = hub.Config.Folder;
            lastOffered = new byte[8];
        }

        // Cached offsets that stay the same per session.
        private ulong BoxStartOffset;
        private ulong OverworldOffset;
        private ulong PortalOffset;
        private ulong ConnectedOffset;
        private ulong TradePartnerNIDOffset;
        private ulong TradePartnerOfferedOffset;

        // Store the current save's OT and TID/SID for comparison.
        private string OT = string.Empty;
        private uint DisplaySID;
        private uint DisplayTID;

        // Stores whether we returned all the way to the overworld, which repositions the cursor.
        private bool StartFromOverworld = true;
        // Stores whether the last trade was Distribution with fixed code, in which case we don't need to re-enter the code.
        private bool LastTradeDistributionFixed;
        private bool LastTradeCloneFixed;

        // Track the last Pokémon we were offered since it persists between trades.
        private byte[] lastOffered;

        // Track the last distribution code used to check for remote changes.
        private int LastCodeUsed;

        public override async Task MainLoop(CancellationToken token)
        {
            try
            {
                await InitializeHardware(Hub.Config.Trade, token).ConfigureAwait(false);

                Log("Identifying trainer data of the host console.");
                var sav = await IdentifyTrainer(token).ConfigureAwait(false);
                OT = sav.OT;
                DisplaySID = sav.DisplaySID;
                DisplayTID = sav.DisplayTID;
                RecentTrainerCache.SetRecentTrainer(sav);
                await InitializeSessionOffsets(token).ConfigureAwait(false);

                // Force the bot to go through all the motions again on its first pass.
                StartFromOverworld = true;
                LastTradeDistributionFixed = false;
                LastTradeCloneFixed = false;

                Log($"Starting main {nameof(PokeTradeBotSV)} loop.");
                await InnerLoop(sav, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(e.Message);
            }

            Log($"Ending {nameof(PokeTradeBotSV)} loop.");
            await HardStop().ConfigureAwait(false);
        }

        public override async Task HardStop()
        {
            UpdateBarrier(false);
            await CleanExit(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task InnerLoop(SAV9SV sav, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Config.IterateNextRoutine();
                var task = Config.CurrentRoutineType switch
                {
                    PokeRoutineType.Idle => DoNothing(token),
                    _ => DoTrades(sav, token),
                };
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    if (e.StackTrace != null)
                        Connection.LogError(e.StackTrace);
                    var attempts = Hub.Config.Timings.ReconnectAttempts;
                    var delay = Hub.Config.Timings.ExtraReconnectDelay;
                    var protocol = Config.Connection.Protocol;
                    if (!await TryReconnect(attempts, delay, protocol, token).ConfigureAwait(false))
                        return;
                }
            }
        }

        private async Task DoNothing(CancellationToken token)
        {
            Log("No task assigned. Waiting for new task assignment.");
            while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.Idle)
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }

        private async Task DoTrades(SAV9SV sav, CancellationToken token)
        {
            var type = Config.CurrentRoutineType;
            int waitCounter = 0;
            await SetCurrentBox(0, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested && Config.NextRoutineType == type)
            {
                var (detail, priority) = GetTradeData(type);
                if (detail is null)
                {
                    await WaitForQueueStep(waitCounter++, token).ConfigureAwait(false);
                    continue;
                }
                waitCounter = 0;

                detail.IsProcessing = true;
                string tradetype = $" ({detail.Type})";
                Log($"Starting next {type}{tradetype} Bot Trade. Getting data...");
                Hub.Config.Stream.StartTrade(this, detail, Hub);
                Hub.Queues.StartTrade(this, detail);

                await PerformTrade(sav, detail, type, priority, token).ConfigureAwait(false);
            }
        }

        private async Task WaitForQueueStep(int waitCounter, CancellationToken token)
        {
            if (waitCounter == 0)
            {
                // Updates the assets.
                Hub.Config.Stream.IdleAssets(this);
                Log("Nothing to check, waiting for new users...");
            }

            await Task.Delay(1_000, token).ConfigureAwait(false);
        }

        protected virtual (PokeTradeDetail<PK9>? detail, uint priority) GetTradeData(PokeRoutineType type)
        {
            if (Hub.Queues.TryDequeue(type, out var detail, out var priority))
                return (detail, priority);
            if (Hub.Queues.TryDequeueClone(out detail))
                return (detail, PokeTradePriorities.TierFree);
            if (Hub.Queues.TryDequeueLedy(out detail))
                return (detail, PokeTradePriorities.TierFree);
            return (null, PokeTradePriorities.TierFree);
        }

        private async Task PerformTrade(SAV9SV sav, PokeTradeDetail<PK9> detail, PokeRoutineType type, uint priority, CancellationToken token)
        {
            PokeTradeResult result;
            try
            {
                result = await PerformLinkCodeTrade(sav, detail, token).ConfigureAwait(false);
                if (result == PokeTradeResult.Success)
                {
                    if (detail.Type == PokeTradeType.Clone)
                        EchoCloneTradeResult(detail, result);
                    return;
                }
            }
            catch (SocketException socket)
            {
                Log(socket.Message);
                result = PokeTradeResult.ExceptionConnection;
                HandleAbortedTrade(detail, type, priority, result);
                throw; // let this interrupt the trade loop. re-entering the trade loop will recheck the connection.
            }
            catch (Exception e)
            {
                Log(e.Message);
                result = PokeTradeResult.ExceptionInternal;
            }

            HandleAbortedTrade(detail, type, priority, result);
        }

        private void HandleAbortedTrade(PokeTradeDetail<PK9> detail, PokeRoutineType type, uint priority, PokeTradeResult result)
        {
            detail.IsProcessing = false;
            if (detail.Type == PokeTradeType.Clone)
                EchoCloneTradeResult(detail, result);
            if (result.ShouldAttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry && !(detail.Type == PokeTradeType.Clone && Config.InitialRoutine == PokeRoutineType.FlexTrade && Hub.Config.Clone.CloneWhileIdle))
            {
                detail.IsRetry = true;
                Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradePriorities.Tier2));
                detail.SendNotification(this, "Oops! Something happened. I'll requeue you for another attempt.");
            }
            else
            {
                detail.SendNotification(this, $"Oops! Something happened. Canceling the trade: {result}.");
                detail.TradeCanceled(this, result);
            }
        }

        private static void EchoCloneTradeResult(PokeTradeDetail<PK9> detail, PokeTradeResult result)
        {
            string content = result == PokeTradeResult.Success ? "Trade Completed" : "Trade Failed";
            string trainerData = $"{detail.Trainer.TrainerName}-{detail.Trainer.ID}";
            string cloneData = "Clone request data is:\n";
            string embedColor = result == PokeTradeResult.Success ? "39168" : "16711680";

            if (detail.SwapInfoList != null)
            {
                var requests = detail.SwapInfoList.Summarize();
                var requestBlock = string.Join("\n", requests);
                cloneData += requestBlock;
            }
            else
            {
                cloneData += "Regular clone requested.";
            }

            string requestedMon = detail.TradeData.FileNameWithoutExtension;
            string embedData = $"Trainer: {trainerData}\n\n{cloneData}\n\nRequested mon: {requestedMon}";
            string payload = $"{{'embeds': [{{'type': 'rich', 'title': '{content}', 'color': '{embedColor}', 'description': '{embedData}'}}]}}";
            EchoUtil.EchoEmbed(payload);
        }

        private async Task<PokeTradeResult> PerformLinkCodeTrade(SAV9SV sav, PokeTradeDetail<PK9> poke, CancellationToken token)
        {
            // Update Barrier Settings
            UpdateBarrier(poke.IsSynchronized);
            poke.TradeInitialize(this);
            Hub.Config.Stream.EndEnterCode(this);

            // StartFromOverworld can be true on first pass or if something went wrong last trade.
            if (StartFromOverworld && !await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                await RecoverToOverworld(token).ConfigureAwait(false);

            // Handles getting into the portal. Will retry this until successful.
            // if we're not starting from overworld, then ensure we're online before opening link trade -- will break the bot otherwise.
            // If we're starting from overworld, then ensure we're online before opening the portal.
            if (!StartFromOverworld && !await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                await RecoverToOverworld(token).ConfigureAwait(false);
                if (!await ConnectAndEnterPortal(token).ConfigureAwait(false))
                {
                    await RecoverToOverworld(token).ConfigureAwait(false);
                    return PokeTradeResult.RecoverStart;
                }
            }
            else if (StartFromOverworld && !await ConnectAndEnterPortal(token).ConfigureAwait(false))
            {
                await RecoverToOverworld(token).ConfigureAwait(false);
                return PokeTradeResult.RecoverStart;
            }

            var toSend = poke.TradeData;
            if (toSend.Species != 0)
                await SetBoxPokemonAbsolute(BoxStartOffset, toSend, token, sav).ConfigureAwait(false);

            // Assumes we're freshly in the Portal and the cursor is over Link Trade.
            Log("Selecting Link Trade.");

            await Click(A, 1_500, token).ConfigureAwait(false);
            // Make sure we clear any Link Codes if we're not in Distribution with fixed code, and it wasn't entered last round.
            bool isFixedTrade = LastTradeCloneFixed || LastTradeDistributionFixed;
            bool requireCodeEntryFixed = isFixedTrade && poke.Code != LastCodeUsed;
            if (!isFixedTrade || requireCodeEntryFixed)
            {
                await Click(X, 1_000, token).ConfigureAwait(false);
                await Click(PLUS, 1_000, token).ConfigureAwait(false);

                // Loading code entry.
                if (poke.Type != PokeTradeType.Random | (poke.Type != PokeTradeType.Clone && Hub.Config.Clone.CloneWhileIdle))
                    Hub.Config.Stream.StartEnterCode(this);
                await Task.Delay(Hub.Config.Timings.ExtraTimeOpenCodeEntry, token).ConfigureAwait(false);

                var code = poke.Code;
                Log($"Entering Link Trade code: {code:0000 0000}...");
                await EnterLinkCode(code, Hub.Config, token).ConfigureAwait(false);

                await Click(PLUS, 3_000, token).ConfigureAwait(false);
                StartFromOverworld = false;
                LastCodeUsed = code;
            }

            if (poke.Type == PokeTradeType.Random)
            {
                LastTradeDistributionFixed = !Hub.Config.Distribution.RandomCode;
            }

            if (poke.Type == PokeTradeType.Clone && Hub.Config.Clone.CloneWhileIdle)
            {
                LastTradeCloneFixed = !Hub.Config.Distribution.RandomCode;
            }

            // Search for a trade partner for a Link Trade.
            await Click(A, 1_000, token).ConfigureAwait(false);

            // Clear it so we can detect it loading.
            await ClearTradePartnerNID(TradePartnerNIDOffset, token).ConfigureAwait(false);

            // Wait for Barrier to trigger all bots simultaneously.
            WaitAtBarrierIfApplicable(token);
            await Click(A, 1_000, token).ConfigureAwait(false);

            poke.TradeSearching(this);

            // Wait for a Trainer...
            var partnerFound = await WaitForTradePartner(token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                StartFromOverworld = true;
                LastTradeDistributionFixed = false;
                LastTradeCloneFixed = false;
                await ExitTradeToPortal(false, token).ConfigureAwait(false);
                return PokeTradeResult.RoutineCancel;
            }
            if (!partnerFound)
            {
                if (!await RecoverToPortal(token).ConfigureAwait(false))
                {
                    Log("Failed to recover to portal.");
                    await RecoverToOverworld(token).ConfigureAwait(false);
                }
                return PokeTradeResult.NoTrainerFound;
            }

            Hub.Config.Stream.EndEnterCode(this);

            // Wait until we get into the box.
            var cnt = 0;
            while (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(0_500, token).ConfigureAwait(false);
                if (++cnt > 20) // Didn't make it in after 10 seconds.
                {
                    await Click(A, 1_000, token).ConfigureAwait(false); // Ensures we dismiss a popup.
                    if (!await RecoverToPortal(token).ConfigureAwait(false))
                    {
                        Log("Failed to recover to portal.");
                        await RecoverToOverworld(token).ConfigureAwait(false);
                    }
                    return PokeTradeResult.RecoverOpenBox;
                }
            }
            await Task.Delay(3_000 + Hub.Config.Timings.ExtraTimeOpenBox, token).ConfigureAwait(false);

            var tradePartner = await GetTradePartnerInfo(token).ConfigureAwait(false);
            var trainerNID = await GetTradePartnerNID(TradePartnerNIDOffset, token).ConfigureAwait(false);
            var trainer = new PartnerDataHolderSV(trainerNID, tradePartner);
            RecordUtil<PokeTradeBotSWSH>.Record($"Initiating\t{trainerNID:X16}\t{tradePartner.TrainerName}\t{poke.Trainer.TrainerName}\t{poke.Trainer.ID}\t{poke.ID}\t{toSend.EncryptionConstant:X8}");
            Log($"Found Link Trade partner: {tradePartner.TrainerName}-{tradePartner.TID7} (ID: {trainerNID})");

            var partnerCheck = await CheckPartnerReputation(this, poke, trainerNID, tradePartner.TrainerName, AbuseSettings, token);
            if (partnerCheck != PokeTradeResult.Success)
            {
                await Click(A, 1_000, token).ConfigureAwait(false); // Ensures we dismiss a popup.
                await ExitTradeToPortal(false, token).ConfigureAwait(false);
                return partnerCheck;
            }

            bool isDistribution = false;
            if (poke.Type == PokeTradeType.Random || poke.Type == PokeTradeType.Clone)
                isDistribution = true;
            var list = isDistribution ? PreviousUsersDistribution : PreviousUsers;

            poke.SendNotification(this, $"Found Link Trade partner: {tradePartner.TrainerName}. Waiting for a Pokémon...");

            int multiTrade = 0;
            while (multiTrade < Hub.Config.Clone.TradesPerEncounter)
            {

                if (multiTrade > 0)
                {
                    list.TryRegister(trainerNID, tradePartner.TrainerName);
                }

                // Hard check to verify that the offset changed from the last thing offered from the previous trade.
                // This is because box opening times can vary per person, the offset persists between trades, and can also change offset between trades.
                
                var tradeOffered = await ReadUntilChanged(TradePartnerOfferedOffset, lastOffered, 10_000, 0_500, false, true, token).ConfigureAwait(false);
                Log($"Trade partner offered offset is {TradePartnerOfferedOffset}");
                if (!tradeOffered)
                {
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return PokeTradeResult.TrainerTooSlow;
                }

                if (poke.Type == PokeTradeType.Dump)
                {
                    var result = await ProcessDumpTradeAsync(poke, token).ConfigureAwait(false);
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return result;
                }

                // Wait for user input...
                var offered = await ReadUntilPresent(TradePartnerOfferedOffset, 25_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
                var oldEC = await SwitchConnection.ReadBytesAbsoluteAsync(TradePartnerOfferedOffset, 8, token).ConfigureAwait(false);
                if (offered == null || offered.Species < 1 || !offered.ChecksumValid)
                {
                    Log("Trade ended because a valid Pokémon was not offered.");
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return PokeTradeResult.TrainerTooSlow;
                }

                PokeTradeResult update;
                (toSend, update) = await GetEntityToSend(sav, poke, offered, oldEC, toSend, trainer, token).ConfigureAwait(false);
                if (update != PokeTradeResult.Success)
                {
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return update;
                }

                Log("Confirming trade.");
                var tradeResult = await ConfirmAndStartTrading(poke, token).ConfigureAwait(false);
                if (tradeResult != PokeTradeResult.Success)
                {
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return tradeResult;
                }

                if (token.IsCancellationRequested)
                {
                    StartFromOverworld = true;
                    LastTradeDistributionFixed = false;
                    LastTradeCloneFixed = false;
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return PokeTradeResult.RoutineCancel;
                }

                // Trade was Successful!
                var received = await ReadPokemon(BoxStartOffset, BoxFormatSlotSize, token).ConfigureAwait(false);
                // Pokémon in b1s1 is same as the one they were supposed to receive (was never sent).
                if (SearchUtil.HashByDetails(received) == SearchUtil.HashByDetails(toSend) && received.Checksum == toSend.Checksum)
                {
                    Log("User did not complete the trade.");
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return PokeTradeResult.TrainerTooSlow;
                }

                // As long as we got rid of our inject in b1s1, assume the trade went through.
                Log("User completed the trade.");
                poke.TradeFinished(this, received);

                // Only log if we completed the trade.
                UpdateCountsAndExport(poke, received, toSend);


            // Sometimes they offered another mon, so store that immediately upon leaving Union Room.
            lastOffered = await SwitchConnection.ReadBytesAbsoluteAsync(TradePartnerOfferedOffset, 8, token).ConfigureAwait(false);

                if (poke.Type == PokeTradeType.Random || (poke.Type == PokeTradeType.Clone && Config.InitialRoutine == PokeRoutineType.FlexTrade))
                {
                    multiTrade++;
                } else
                {
                    multiTrade = Hub.Config.Clone.TradesPerEncounter;
                }

                if (multiTrade < Hub.Config.Clone.TradesPerEncounter)
                {
                    await Task.Delay(Hub.Config.Timings.ExtraTimeMultiTrade, token).ConfigureAwait(false);
                }
            }

            // Log for Trade Abuse tracking.
            LogSuccessfulTrades(poke, trainerNID, tradePartner.TrainerName);

            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return PokeTradeResult.Success;
        }

        private void UpdateCountsAndExport(PokeTradeDetail<PK9> poke, PK9 received, PK9 toSend)
        {
            var counts = TradeSettings;
            if (poke.Type == PokeTradeType.Random)
                counts.AddCompletedDistribution();
            else if (poke.Type == PokeTradeType.Clone)
                counts.AddCompletedClones();
            else
                counts.AddCompletedTrade();

            if (poke.SwapInfoList != null)
            {
                foreach (CloneSwapInfo swap in poke.SwapInfoList)
                {
                    switch (swap.SwapType)
                    {
                        case CloneSwapType.TeraSwap:
                            counts.AddCompletedTeraSwaps();
                            break;
                        case CloneSwapType.BallSwap:
                            counts.AddCompletedBallSwaps();
                            break;
                        case CloneSwapType.EVSpread:
                            counts.AddCompletedEVSwaps();
                            break;
                        case CloneSwapType.GennedRequest:
                            counts.AddCompletedGennedSwaps();
                            break;
                        case CloneSwapType.NicknameClear:
                            counts.AddCompletedNameRemoves();
                            break;
                        case CloneSwapType.ItemRequest:
                            counts.AddCompletedItemSwaps();
                            break;
                        case CloneSwapType.DistroRequest:
                            counts.AddCompletedDistroSwaps();
                            break;
                        case CloneSwapType.OTSwap:
                            counts.AddCompletedOTSwaps();
                            break;
                    }
                }
            }

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
            {
                var subfolder = poke.Type.ToString().ToLower();
                DumpPokemon(DumpSetting.DumpFolder, subfolder, received); // received by bot
                if (poke.Type is PokeTradeType.Specific or PokeTradeType.Clone)
                    DumpPokemon(DumpSetting.DumpFolder, "traded", toSend); // sent to partner
            }
        }

        private async Task<PokeTradeResult> ConfirmAndStartTrading(PokeTradeDetail<PK9> detail, CancellationToken token)
        {
            // We'll keep watching B1S1 for a change to indicate a trade started -> should try quitting at that point.
            var oldEC = await SwitchConnection.ReadBytesAbsoluteAsync(BoxStartOffset, 8, token).ConfigureAwait(false);

            await Click(A, 3_000, token).ConfigureAwait(false);
            for (int i = 0; i < Hub.Config.Trade.MaxTradeConfirmTime; i++)
            {
                if (await IsUserBeingShifty(detail, token).ConfigureAwait(false))
                    return PokeTradeResult.SuspiciousActivity;

                // We can fall out of the box if the user offers, then quits.
                if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                    return PokeTradeResult.TrainerLeft;

                await Click(A, 1_000, token).ConfigureAwait(false);

                // EC is detectable at the start of the animation.
                var newEC = await SwitchConnection.ReadBytesAbsoluteAsync(BoxStartOffset, 8, token).ConfigureAwait(false);
                if (!newEC.SequenceEqual(oldEC))
                {
                    await Task.Delay(25_000, token).ConfigureAwait(false);
                    return PokeTradeResult.Success;
                }
            }
            // If we don't detect a B1S1 change, the trade didn't go through in that time.
            return PokeTradeResult.TrainerTooSlow;
        }

        // Upon connecting, their Nintendo ID will instantly update.
        protected virtual async Task<bool> WaitForTradePartner(CancellationToken token)
        {
            Log("Waiting for trainer...");
            int ctr = (Hub.Config.Trade.TradeWaitTime * 1_000) - 2_000;
            await Task.Delay(2_000, token).ConfigureAwait(false);
            while (ctr > 0)
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                ctr -= 1_000;
                var newNID = await GetTradePartnerNID(TradePartnerNIDOffset, token).ConfigureAwait(false);
                if (newNID != 0)
                {
                    TradePartnerOfferedOffset = await SwitchConnection.PointerAll(Offsets.LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
                    return true;
                }

                // Fully load into the box.
                await Task.Delay(1_000, token).ConfigureAwait(false);
            }
            return false;
        }

        // If we can't manually recover to overworld, reset the game.
        // Try to avoid pressing A which can put us back in the portal with the long load time.
        private async Task<bool> RecoverToOverworld(CancellationToken token)
        {
            if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                return true;

            Log("Attempting to recover to overworld.");
            var attempts = 0;
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                attempts++;
                if (attempts >= 30)
                    break;

                await Click(B, 1_300, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;

                await Click(B, 2_000, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;

                await Click(A, 1_300, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;
            }

            // We didn't make it for some reason.
            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                Log("Failed to recover to overworld, rebooting the game.");
                await RestartGameSV(token).ConfigureAwait(false);
            }
            await Task.Delay(1_000, token).ConfigureAwait(false);

            // Force the bot to go through all the motions again on its first pass.
            StartFromOverworld = true;
            LastTradeDistributionFixed = false;
            LastTradeCloneFixed = false;
            return true;
        }

        // If we didn't find a trainer, we're still in the portal but there can be 
        // different numbers of pop-ups we have to dismiss to get back to when we can trade.
        // Rather than resetting to overworld, try to reset out of portal and immediately go back in.
        private async Task<bool> RecoverToPortal(CancellationToken token)
        {
            Log("Reorienting to Poké Portal.");
            var attempts = 0;
            while (await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
            {
                await Click(B, 1_500, token).ConfigureAwait(false);
                if (++attempts >= 30)
                {
                    Log("Failed to recover to Poké Portal.");
                    return false;
                }
            }

            // Should be in the X menu hovered over Poké Portal.
            await Click(A, 1_000, token).ConfigureAwait(false);

            return await SetUpPortalCursor(token).ConfigureAwait(false);
        }

        // Should be used from the overworld. Opens X menu, attempts to connect online, and enters the Portal.
        // The cursor should be positioned over Link Trade.
        private async Task<bool> ConnectAndEnterPortal(CancellationToken token)
        {
            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                await RecoverToOverworld(token).ConfigureAwait(false);

            Log("Opening the Poké Portal.");

            // Open the X Menu.
            await Click(X, 1_000, token).ConfigureAwait(false);

            // Handle the news popping up.
            if (await SwitchConnection.IsProgramRunning(LibAppletWeID, token).ConfigureAwait(false))
            {
                Log("News detected, will close once it's loaded!");
                await Task.Delay(5_000, token).ConfigureAwait(false);
                await Click(B, 2_000, token).ConfigureAwait(false);
            }

            // Scroll to the bottom of the Main Menu so we don't need to care if Picnic is unlocked.
            await Click(DRIGHT, 0_300, token).ConfigureAwait(false);
            await PressAndHold(DDOWN, 1_000, 1_000, token).ConfigureAwait(false);
            await Click(DUP, 0_200, token).ConfigureAwait(false);
            await Click(DUP, 0_200, token).ConfigureAwait(false);
            await Click(DUP, 0_200, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);

            return await SetUpPortalCursor(token).ConfigureAwait(false);
        }

        // Waits for the Portal to load (slow) and then moves the cursor down to Link Trade.
        private async Task<bool> SetUpPortalCursor(CancellationToken token)
        {
            // Wait for the portal to load.
            var attempts = 0;
            while (!await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(0_500, token).ConfigureAwait(false);
                if (++attempts > 20)
                {
                    Log("Failed to load the Poké Portal.");
                    return false;
                }
            }
            await Task.Delay(2_000 + Hub.Config.Timings.ExtraTimeLoadPortal, token).ConfigureAwait(false);

            // Connect online if not already.
            if (!await ConnectToOnline(Hub.Config, token).ConfigureAwait(false))
            {
                Log("Failed to connect to online.");
                return false; // Failed, either due to connection or softban.
            }

            // Handle the news popping up.
            if (await SwitchConnection.IsProgramRunning(LibAppletWeID, token).ConfigureAwait(false))
            {
                Log("News detected, will close once it's loaded!");
                await Task.Delay(5_000, token).ConfigureAwait(false);
                await Click(B, 2_000 + Hub.Config.Timings.ExtraTimeLoadPortal, token).ConfigureAwait(false);
            }

            Log("Adjusting the cursor in the Portal.");
            // Move down to Link Trade.
            await Click(DDOWN, 0_300, token).ConfigureAwait(false);
            await Click(DDOWN, 0_300, token).ConfigureAwait(false);
            return true;
        }

        // Connects online if not already. Assumes the user to be in the X menu to avoid a news screen.
        private async Task<bool> ConnectToOnline(PokeTradeHubConfig config, CancellationToken token)
        {
            if (await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
                return true;

            await Click(L, 1_000, token).ConfigureAwait(false);
            await Click(A, 4_000, token).ConfigureAwait(false);

            var wait = 0;
            while (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(0_500, token).ConfigureAwait(false);
                if (++wait > 30) // More than 15 seconds without a connection.
                    return false;
            }

            // There are several seconds after connection is established before we can dismiss the menu.
            await Task.Delay(3_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
            return true;
        }

        private async Task ExitTradeToPortal(bool unexpected, CancellationToken token)
        {
            if (await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
                return;

            if (unexpected)
                Log("Unexpected behavior, recovering to Portal.");

            // Ensure we're not in the box first.
            // Takes a long time for the Portal to load up, so once we exit the box, wait 5 seconds.
            Log("Leaving the box...");
            var attempts = 0;
            while (await IsInBox(PortalOffset, token).ConfigureAwait(false))
            {
                await Click(B, 1_000, token).ConfigureAwait(false);
                if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                {
                    await Task.Delay(5_000, token).ConfigureAwait(false);
                    break;
                }

                await Click(A, 1_000, token).ConfigureAwait(false);
                if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                {
                    await Task.Delay(5_000, token).ConfigureAwait(false);
                    break;
                }

                await Click(B, 1_000, token).ConfigureAwait(false);
                if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                {
                    await Task.Delay(5_000, token).ConfigureAwait(false);
                    break;
                }

                // Didn't make it out of the box for some reason.
                if (++attempts > 20)
                {
                    Log("Failed to exit box, rebooting the game.");
                    if (!await RecoverToOverworld(token).ConfigureAwait(false))
                        await RestartGameSV(token).ConfigureAwait(false);
                    await ConnectAndEnterPortal(token).ConfigureAwait(false);
                    return;
                }
            }

            // Wait for the portal to load.
            Log("Waiting on the portal to load...");
            attempts = 0;
            while (!await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                if (await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
                    break;

                // Didn't make it into the portal for some reason.
                if (++attempts > 40)
                {
                    Log("Failed to load the portal, rebooting the game.");
                    if (!await RecoverToOverworld(token).ConfigureAwait(false))
                        await RestartGameSV(token).ConfigureAwait(false);
                    await ConnectAndEnterPortal(token).ConfigureAwait(false);
                    return;
                }
            }
            await Task.Delay(2_000, token).ConfigureAwait(false);
        }

        // These don't change per session and we access them frequently, so set these each time we start.
        private async Task InitializeSessionOffsets(CancellationToken token)
        {
            Log("Caching session offsets...");
            BoxStartOffset = await SwitchConnection.PointerAll(Offsets.BoxStartPokemonPointer, token).ConfigureAwait(false);
            OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
            PortalOffset = await SwitchConnection.PointerAll(Offsets.PortalBoxStatusPointer, token).ConfigureAwait(false);
            ConnectedOffset = await SwitchConnection.PointerAll(Offsets.IsConnectedPointer, token).ConfigureAwait(false);
            TradePartnerNIDOffset = await SwitchConnection.PointerAll(Offsets.LinkTradePartnerNIDPointer, token).ConfigureAwait(false);
        }

        // todo: future
        protected virtual async Task<bool> IsUserBeingShifty(PokeTradeDetail<PK9> detail, CancellationToken token)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return false;
        }

        private async Task RestartGameSV(CancellationToken token)
        {
            await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
            await InitializeSessionOffsets(token).ConfigureAwait(false);
        }

        private async Task<PokeTradeResult> ProcessDumpTradeAsync(PokeTradeDetail<PK9> detail, CancellationToken token)
        {
            int ctr = 0;
            var time = TimeSpan.FromSeconds(Hub.Config.Trade.MaxDumpTradeTime);
            var start = DateTime.Now;

            var pkprev = new PK9();
            var bctr = 0;
            while (ctr < Hub.Config.Trade.MaxDumpsPerTrade && DateTime.Now - start < time)
            {
                if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                    break;
                if (bctr++ % 3 == 0)
                    await Click(B, 0_100, token).ConfigureAwait(false);

                // Wait for user input... Needs to be different from the previously offered Pokémon.
                var pk = await ReadUntilPresent(TradePartnerOfferedOffset, 3_000, 0_050, BoxFormatSlotSize, token).ConfigureAwait(false);
                if (pk == null || pk.Species < 1 || !pk.ChecksumValid || SearchUtil.HashByDetails(pk) == SearchUtil.HashByDetails(pkprev))
                    continue;

                // Save the new Pokémon for comparison next round.
                pkprev = pk;

                // Send results from separate thread; the bot doesn't need to wait for things to be calculated.
                if (DumpSetting.Dump)
                {
                    var subfolder = detail.Type.ToString().ToLower();
                    DumpPokemon(DumpSetting.DumpFolder, subfolder, pk); // received
                }

                var la = new LegalityAnalysis(pk);
                var verbose = $"```{la.Report(true)}```";
                Log($"Shown Pokémon is: {(la.Valid ? "Valid" : "Invalid")}.");

                ctr++;
                var msg = Hub.Config.Trade.DumpTradeLegalityCheck ? verbose : $"File {ctr}";

                // Extra information about trainer data for people requesting with their own trainer data.
                var ot = pk.OT_Name;
                var ot_gender = pk.OT_Gender == 0 ? "Male" : "Female";
                var tid = pk.GetDisplayTID().ToString(pk.GetTrainerIDFormat().GetTrainerIDFormatStringTID());
                var sid = pk.GetDisplaySID().ToString(pk.GetTrainerIDFormat().GetTrainerIDFormatStringSID());
                msg += $"\n**Trainer Data**\n```OT: {ot}\nOTGender: {ot_gender}\nTID: {tid}\nSID: {sid}```";

                // Extra information for shiny eggs, because of people dumping to skip hatching.
                var eggstring = pk.IsEgg ? "Egg " : string.Empty;
                msg += pk.IsShiny ? $"\n**This Pokémon {eggstring}is shiny!**" : string.Empty;
                detail.SendNotification(this, pk, msg);
            }

            Log($"Ended Dump loop after processing {ctr} Pokémon.");
            if (ctr == 0)
                return PokeTradeResult.TrainerTooSlow;

            TradeSettings.AddCompletedDumps();
            detail.Notifier.SendNotification(this, detail, $"Dumped {ctr} Pokémon.");
            detail.Notifier.TradeFinished(this, detail, detail.TradeData); // blank PK9
            return PokeTradeResult.Success;
        }

        private async Task<TradePartnerSV> GetTradePartnerInfo(CancellationToken token)
        {
            // We're able to see both users' MyStatus, but one of them will be ourselves.
            var trader_info = await GetTradePartnerMyStatus(Offsets.Trader1MyStatusPointer, token).ConfigureAwait(false);
            if (trader_info.OT == OT && trader_info.DisplaySID == DisplaySID && trader_info.DisplayTID == DisplayTID) // This one matches ourselves.
                trader_info = await GetTradePartnerMyStatus(Offsets.Trader2MyStatusPointer, token).ConfigureAwait(false);
            return new TradePartnerSV(trader_info);
        }

        protected virtual async Task<(PK9 toSend, PokeTradeResult check)> GetEntityToSend(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, byte[] oldEC, PK9 toSend, PartnerDataHolderSV partnerID, CancellationToken token)
        {
            return poke.Type switch
            {
                PokeTradeType.Random => await HandleRandomLedy(sav, poke, offered, toSend, partnerID, token).ConfigureAwait(false),
                PokeTradeType.Clone => await HandleClone(sav, poke, offered, oldEC, partnerID, token).ConfigureAwait(false),
                _ => (toSend, PokeTradeResult.Success),
            };
        }

        private CloneSwapInfoList GetCloneSwapInfo(PK9 offered)
        {
            var config = Hub.Config.Clone;
            CloneSwapInfoList swapInfos = new();
            var itemTriggers = new List<int>
            {
                (int)config.ItemSwapItem,
                (int)config.OTSwapItem,
                (int)config.NickSwapItem,
                (int)config.DistroSwapItem,
                (int)config.GennedSwapItem,
            };
            bool evNickname = offered.Nickname.All(c => "M0SA".Contains(c)) && offered.Nickname.Length == 6;
            bool evHexNickname = offered.Nickname.All(c => "0123456789ABCDEFSN".Contains(c)) && offered.Nickname.Length == 12;
            bool evReset = offered.Nickname == "Reset";
            string item = GameInfo.GetStrings(1).Item[offered.HeldItem];
            if (offered.HeldItem == (int)config.ItemSwapItem)
            {
                swapInfos.Update(GetCloneSwap(CloneSwapType.ItemRequest, "Clone", offered.Nickname));
            }
            if (offered.HeldItem == (int)config.OTSwapItem)
            {
                swapInfos.Update(GetCloneSwap(CloneSwapType.OTSwap, "Clone"));
            }
            if (offered.HeldItem == (int)config.NickSwapItem)
            {
                swapInfos.Update(GetCloneSwap(CloneSwapType.NicknameClear, "Clone"));
            }
            if (offered.HeldItem == (int)config.DistroSwapItem)
            {
                swapInfos.Update(GetCloneSwap(CloneSwapType.DistroRequest, "Clone"));
            }
            if (offered.HeldItem == (int)config.GennedSwapItem)
            {
                bool genNickname = offered.Nickname.All(c => "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz+-".Contains(c));
                bool genNickLength = offered.Nickname.Length is 6 or 12;
                if (genNickname && genNickLength)
                    swapInfos.Update(GetCloneSwap(CloneSwapType.GennedRequest, "Clone", offered.Nickname));
            }
            if (offered.HeldItem != 0 && !itemTriggers.Contains(offered.HeldItem))
            {
                string[] itemString = item.Split(' ');
                if (itemString.Length > 1)
                {
                    if (itemString[1] == "Ball")
                    {
                        swapInfos.Update(GetCloneSwap(CloneSwapType.BallSwap, "Clone", itemString[0]));
                    }
                    if (itemString[1] == "Tera")
                    {
                        swapInfos.Update(GetCloneSwap(CloneSwapType.TeraSwap, "Clone", itemString[0]));
                    }
                }
            }
            if (evNickname || evHexNickname || evReset)
            {
                if (offered.HeldItem != (int)config.GennedSwapItem)
                {
                    swapInfos.Update(GetCloneSwap(CloneSwapType.EVSpread, "Clone", offered.Nickname));
                    swapInfos.Update(GetCloneSwap(CloneSwapType.NicknameClear, "Auto"));
                }
            }

            CloneSwapInfo? isBallTeraName = CheckBallTeraName(offered, "Clone");
            if (isBallTeraName != null)
            {
                swapInfos.Update(isBallTeraName);
                swapInfos.Update(GetCloneSwap(CloneSwapType.NicknameClear, "Auto"));
            }

            return swapInfos;
        }

        private static CloneSwapInfo? CheckBallTeraName(PK9 offered, string requestMon)
        {
            string nick = offered.Nickname;
            if (nick == "Poké")
                nick = "Poke";
            var isDigits = int.TryParse(nick, out _);
            if (isDigits)
                return null;
            var isTera = Enum.TryParse(nick, true, out MoveType Tera);
            var isBall = Enum.TryParse(nick, true, out Ball Ball);
            if (isTera && nick != "Any")
            {
                return GetCloneSwap(CloneSwapType.TeraSwap, requestMon, Tera.ToString());
            }
            if (isBall)
            {
                return GetCloneSwap(CloneSwapType.BallSwap, requestMon, Ball.ToString());
            }
            return null;
        }

        private (PK9 clone, PokeTradeResult check) HandleTeraSwap(string type, PK9 clone)
        {
            if (clone.IsEgg)
                return (clone, PokeTradeResult.TrainerRequestBad);
            MoveType a;
            try
            {
                a = (MoveType)Enum.Parse(typeof(MoveType), type);
                clone.TeraTypeOverride = a;
                return (clone, PokeTradeResult.Success);
            }
            catch (Exception e)
            {
                Log(e.Message);
                return (clone, PokeTradeResult.TrainerRequestBad);
            }
        }

        private static (PK9 clone, PokeTradeResult check) HandleOTSwap(PK9 clone, PartnerDataHolderSV partner)
        {
            if (clone.Version != 50 && clone.Version != 51)
                return (clone, PokeTradeResult.TrainerRequestBad);

            bool isShiny = clone.IsShiny;

            if ((clone.Species == (int)Species.Koraidon && partner.Game == 51) || (clone.Species == (int)Species.Miraidon && partner.Game == 50))
            {
                clone.OT_Name = partner.TrainerName;
                clone.Language = partner.Language;
                clone.TID16 = (ushort)rnd.Next(1, 65536);
                clone.SID16 = (ushort)rnd.Next(1, 65536);
                clone.OT_Gender = partner.Gender;
            } else
            {
                clone.OT_Name = partner.TrainerName;
                clone.Language = partner.Language;
                clone.DisplayTID = uint.Parse(partner.TID7);
                clone.DisplaySID = uint.Parse(partner.SID7);
                clone.Version = partner.Game;
                clone.OT_Gender = partner.Gender;
            }

            if (isShiny)
            {
                bool isSquareShiny = clone.ShinyXor == 0;
                if (clone.Met_Location == 30024)
                    clone.PID = isSquareShiny ? (((uint)(clone.TID16 ^ clone.SID16) ^ (clone.PID & 0xFFFF) ^ 0) << 16) | (clone.PID & 0xFFFF) : (((uint)(clone.TID16 ^ clone.SID16) ^ (clone.PID & 0xFFFF) ^ 1u) << 16) | (clone.PID & 0xFFFF);                    
                else
                {
                    if (isSquareShiny)
                    {
                        do { clone.SetUnshiny(); clone.SetShiny(); } while (clone.ShinyXor != 0);
                    }
                    else
                    {
                        do { clone.SetUnshiny(); clone.SetShiny(); } while (clone.ShinyXor == 0);
                    }
                }
            }

            if (clone.WasEgg && clone.Egg_Location == 30002)
                clone.Egg_Location = 30023;

            var la = new LegalityAnalysis(clone);
            if (!la.Valid)
                return (clone, PokeTradeResult.TrainerRequestBad);

            return (clone, PokeTradeResult.Success);
        }

        private (PK9 clone, PokeTradeResult check) HandleBallSwap(string ball, PK9 clone)
        {
            Ball b;

            if (clone.Version != 50 && clone.Version != 51)
                return (clone, PokeTradeResult.TrainerRequestBad);

            //Handle items with Ball as second word that aren't actually Balls
            if (ball is "Smoke" or "Iron" or "Light")
                return (clone, PokeTradeResult.Success);

            //Handle Paldea starters not being legal in other balls
            if (clone.Species > 905 && clone.Species < 915)
                return (clone, PokeTradeResult.TrainerRequestBad);

            //Handle Cherish Balls not being available
            if (ball is "Cherish")
                return (clone, PokeTradeResult.TrainerRequestBad);

            //Handle Event Pokemon
            if (clone.FatefulEncounter)
                return (clone, PokeTradeResult.TrainerRequestBad);

            //Handle Balls that aren't released yet in SV
            if (ball is "Sport" or "Safari")
                return (clone, PokeTradeResult.TrainerRequestBad);

            //Handle LA Balls until Home support
            if (ball is "LAPoke" or "LAGreat" or "LAUltra" or "LAFeather" or "LAWing" or "LAJet" or "LAHeavy" or "LALeaden" or "LAGigaton" or "LAOrigin")
                return (clone, PokeTradeResult.TrainerRequestBad);

            //In-game trades from NPCs can't have Balls swapped
            if (clone.Met_Location is 30001)
                return (clone, PokeTradeResult.TrainerRequestBad);

            //GMeowth from Salvatore can't have Ball swapped
            if (clone.Met_Location is 130 or 131)
                if (clone.Met_Level is 5)
                    return (clone, PokeTradeResult.TrainerRequestBad);

            //Master balls don't breed down
            if (clone.WasEgg || clone.IsEgg)
            {
                if (ball is "Master")
                    return (clone, PokeTradeResult.TrainerRequestBad);
            }

            if (ball is "Poké")
                ball = "Poke";

            try
            {
                b = (Ball)Enum.Parse(typeof(Ball), ball);
                clone.Ball = (int)b;
                return (clone, PokeTradeResult.Success);
            }
            catch (Exception e)
            {
                Log(e.Message);
                return (clone, PokeTradeResult.TrainerRequestBad);
            }
        }

        private static (PK9 clone, PokeTradeResult check) HandleNameRemove(PK9 clone)
        {
            if (clone.Met_Location == 30001 || clone.FatefulEncounter || clone.IsEgg)
                return (clone, PokeTradeResult.TrainerRequestBad);
            clone.SetDefaultNickname();
            return (clone, PokeTradeResult.Success);
        }

        private static (PK9 clone, PokeTradeResult check) HandleEVSwap(string info, PK9 clone)
        {
            if (clone.IsEgg)
                return (clone, PokeTradeResult.TrainerRequestBad);
            int[] spread = new int[] { 0, 0, 0, 0, 0, 0 };
            if (info == "Reset")
            {
                clone.SetEVs(spread);
                return (clone, PokeTradeResult.Success);
            } 
            int i = 0, j = 0;
            int maxEV = 510;
            if (info.Length == 6)
            {
                List<int> splitEV = new();
                char[] nickChars = info.ToCharArray();
                foreach (char f in nickChars)
                {
                    if (f is 'M')
                    {
                        spread[i++] = 252;
                    }
                    else if (f is '0')
                    {
                        spread[i++] = 0;
                    }
                    else if (f is 'S')
                    {
                        splitEV.Add(i++);
                    }
                    else if (f is 'A')
                    {
                        spread[i] = clone.GetEV(i++);
                    }
                    else
                    {
                        return (clone, PokeTradeResult.TrainerRequestBad);
                    }
                }

                if (spread.Sum() > maxEV)
                    return (clone, PokeTradeResult.TrainerRequestBad);

                if (splitEV.Count != 0)
                {
                    int split = (maxEV - spread.Sum()) / splitEV.Count;
                    if (split > 252)
                        split = 252;
                    foreach (int e in splitEV)
                    {
                        spread[e] = split;
                    }
                }

                if (spread.Sum() > maxEV)
                    return (clone, PokeTradeResult.TrainerRequestBad);

                clone.SetEVs(spread);
                return (clone, PokeTradeResult.Success);
            } else if (info.Length == 12)
            {
                List<string> nickHexValues = new();
                for (i = 0; i < 12; i += 2)
                {
                    nickHexValues.Add(info.Substring(i, 2));
                }
                foreach (string f in nickHexValues)
                {
                    if (f is "NN")
                        spread[j++] = 0;
                    else if (f is "SS")
                        spread[j] = clone.GetEV(j++);
                    else
                    {
                        int EVValue = Convert.ToInt32(f, 16);
                        if (EVValue > 252)
                            EVValue = 252;
                        spread[j++] = EVValue;
                    }
                }

                if (spread.Sum() > maxEV)
                    return (clone, PokeTradeResult.TrainerRequestBad);

                clone.SetEVs(spread);
                return (clone, PokeTradeResult.Success);
            } else
            {
                return (clone, PokeTradeResult.TrainerRequestBad);
            }
        }

        private (PK9 clone, PokeTradeResult check) HandleItemSwap(string info, PK9 clone)
        {
            if (clone.IsEgg)
                return (clone, PokeTradeResult.TrainerRequestBad);
            bool isParsable = short.TryParse(info, out short itemID);
            if (!isParsable)
            {
                return (clone, PokeTradeResult.TrainerRequestBad);
            }
            itemID -= 1;
            Log($"Requesting item {GameInfo.GetStrings(1).Item[itemID]}");
            bool canHold = ItemRestrictions.IsHeldItemAllowed(itemID, clone.Context);
            if (!canHold)
            {
                return (clone, PokeTradeResult.TrainerRequestBad);
            }
            clone.HeldItem = itemID;
            return (clone, PokeTradeResult.Success);

        }

        private (PK9 clone, PokeTradeResult check) HandleDistroSwap(PK9 clone)
        {
            ulong dummyID = 0;
            var trade = Hub.Ledy.GetLedyTrade(clone, dummyID);
            if (trade != null)
            {
                clone = trade.Receive;
                return (clone, PokeTradeResult.Success);
            }
            return (clone, PokeTradeResult.TrainerRequestBad);
        }

        private static long Base64ToInt64(string convert)
        {
            string digitsStr = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz+-";
            char[] radix64 = digitsStr.ToCharArray();
            int tbase = 64;
            long b10 = 0;
            for (int i = convert.Length; i > 0; i--)
            {
                b10 += Array.IndexOf(radix64, convert[^i]) * (long)Math.Pow(tbase, i - 1);
            }
            return b10;
        }

        private (PK9 pk, PokeTradeResult check) HandleGennedSwap(PK9 offered, PartnerDataHolderSV partner)
        {
            string nature, abiName, ball, formName = "";
            bool hasForm = false, hasGen9 = false, onlyPLA = false;
            PokeTradeResult update;
            var s = GameInfo.Strings;
            string genInput = offered.Nickname[..6];
            long setReqInput = Base64ToInt64(genInput);
            (ushort species, byte form, ushort natureIndex, ushort teraIndex, ushort ballIndex, char scaleChar, bool lowLevel, bool shiny, char genderChar, int abiIndex, update) = ParseSetReq(setReqInput);
            if (species >= (int)Species.MAX_COUNT)
                return (offered, PokeTradeResult.TrainerRequestBad);
            string specName = GameInfo.GetStrings(1).Species[species];
            PersonalInfo9SV formInfo;
            PersonalInfo9SV speciesInfo = formInfo = PersonalTable.SV[species];
            string[] AvailForms = species == (int)Species.Alcremie ? FormConverter.GetAlcremieFormList(s.forms) : FormConverter.GetFormList(species, s.types, s.forms, GameInfo.GenderSymbolASCII, EntityContext.Gen9);
            hasForm = speciesInfo.IsFormWithinRange(form);
            if (hasForm)
            {
                formName = AvailForms[form];
                formInfo = PersonalTable.SV.GetFormEntry(species, form);
            }
            abiName = GameInfo.GetStrings(1).Ability[formInfo.GetAbilityAtIndex(abiIndex)];
            if (natureIndex > 24)
                natureIndex = (ushort)rnd.Next(0, 24);
            nature = GameInfo.GetStrings(1).Natures[natureIndex];
            if (Enum.IsDefined(typeof(Ball), (byte)ballIndex))
                ball = ((Ball)ballIndex).ToString();
            else
                return (offered, PokeTradeResult.TrainerRequestBad);

            int validGender = formInfo.Gender;

            var reqGender = validGender switch
            {
                PersonalInfo.RatioMagicGenderless => 2,
                PersonalInfo.RatioMagicFemale => 1,
                PersonalInfo.RatioMagicMale => 0,
                _ => genderChar == 'M' ? 0 : 1,
            };

            string scaleSet = scaleChar switch
            {
                'T' => ".Scale=0\r\n",
                'J' => ".Scale=255\r\n",
                _ => string.Empty,
            };

            string scaleLog = scaleChar switch
            {
                'T' => "Tiny",
                'J' => "Jumbo",
                _ => "",
            };

            string? genderLog = Enum.GetName(typeof(Gender), reqGender);
            if (genderLog is null)
                return (offered, PokeTradeResult.TrainerRequestBad);            
            string genderSet = reqGender switch
            {
                1 => " (F)\r\n",
                0 => " (M)\r\n",
                _ => "\r\n",
            };
            string shinyLog = shiny ? "Shiny " : "";
            string formLog = formName is not "" ? "-" + formName : "";
            if (formLog.Contains(" ("))
            {
                formLog = formLog.Replace(" (", "-");
                formLog = formLog.Replace(")", "");
            }

            MoveType tera = (MoveType)(byte)teraIndex;

            // Handle specific IV requests
            string ReqIVs = "";
            string[] ivTitles = { "HP", "Atk", "Def", "Spe", "SpA", "SpD" };
            if (offered.Nickname.Length == 12)
            {
                string IVSpread = offered.Nickname[^6..].ToUpper();
                int i = 0; int j;
                foreach (char c in IVSpread)
                {
                    if (c is 'W' or 'X' or 'Y' or 'Z')
                        j = 0;
                    else
                        j = Convert.ToInt16(Base64ToInt64(c.ToString()));
                    if (j < 31)
                        ReqIVs += j + " " + ivTitles[i] + " / ";
                    i++;
                }
            }

            bool raidOnly = false, staticScale = false;

            if (species == (ushort)Species.WalkingWake || species == (ushort)Species.IronLeaves)
            {
                raidOnly = true;
                staticScale = true;
            }

            if (species == (ushort)Species.Miraidon || species == (ushort)Species.Koraidon || species == (ushort)Species.TingLu || species == (ushort)Species.ChienPao || species == (ushort)Species.WoChien || species == (ushort)Species.ChiYu)
                staticScale = true;

            var sav = TrainerSettings.GetSavedTrainerData(GameVersion.SV, 9);

            string genderOTSet = partner.Gender == 0 ? "Male" : "Female";

            bool randID = (species == (ushort)Species.Koraidon && partner.Game != 50) || (species == (ushort)Species.Miraidon && partner.Game != 51);
            uint newID32 = new();
            if (randID)
            {
                uint newSID = (uint)rnd.Next(1, 65536);
                uint newTID = (uint)rnd.Next(1, 65536);
                newID32 = (newSID << 16) | newTID;
            }

            //Check for Gen9 and PLA encounters to account for OT info and Ball later on
            ushort[] genMoves = new ushort[] { 0, 0, 0, 0 };
            GameVersion[] genVersions = new GameVersion[] { GameVersion.SL, GameVersion.VL };
            PK9 genPKM = new() { Species = species, Form = form, Gender = reqGender };
            var encTable = EncounterMovesetGenerator.GenerateEncounters(genPKM, genMoves, genVersions);
            if (encTable.Any())
            {
                hasGen9 = encTable.Any(e => e.Version == GameVersion.SV || e.Version == GameVersion.VL);
                onlyPLA = encTable.All(e => e.Version == GameVersion.PLA);
            }

            if (onlyPLA)
            {
                if (ball != "Poke" && ball != "Great" && ball != "Ultra")
                {
                    string[] ballList = { "Poke", "Great", "Ultra" };
                    ball = ballList[rnd.Next(ballList.Length)];
                }
                ball = "LA" + ball;
            }

            //Check for Vivi PokeBall cause somehow the above doesn't like it
            if (species == (ushort)Species.Vivillon && form == 19)
                hasGen9 = false;

            var genLog = lowLevel ? $"Request is for {shinyLog}{specName}{formLog} ({genderChar}) {scaleLog} with {abiName} and {nature} Nature at lowest legal level." : $"Request is for {shinyLog}{specName}{formLog} ({genderChar}) {scaleLog} with {abiName} and {nature} Nature.";
            Log(genLog);
            Log($"Appearing caught in {ball} Ball with Tera Type {tera}.");

            // Generate basic Showdown Set information
            string showdownSet = "";
            showdownSet += specName;
            if (formName is not "")
                showdownSet += formLog;
            showdownSet += genderSet;            
            showdownSet += "Ability: " + abiName + "\r\n";
            if (shiny)
                showdownSet += "Shiny: Yes\r\n";
            showdownSet += "Ball: " + ball + "\r\n";
            showdownSet += "Tera Type: " + tera.ToString() + "\r\n";
            showdownSet += nature + " Nature\r\n";
            if (ReqIVs != "")
                showdownSet += "IVs: " + ReqIVs + "\r\n";
            if (hasGen9)
            {
                showdownSet += "Language: " + Enum.GetName(typeof(LanguageID), partner.Language) + "\r\n";
                showdownSet += "OT: " + partner.TrainerName + "\r\n";
                showdownSet += randID ? "TID: " + (newID32 % 1000000) + "\r\n" : "TID: " + partner.TID7 + "\r\n";
                showdownSet += randID ? "SID: " + (newID32 / 1000000) + "\r\n" : "SID: " + partner.SID7 + "\r\n";
                showdownSet += "OTGender: " + genderOTSet + "\r\n";
                string boxVersionCheck = species switch
                {
                    (ushort)Species.Koraidon => ".Version=50\r\n",
                    (ushort)Species.Miraidon => ".Version=51\r\n",
                    _ => ".Version=" + partner.Game + "\r\n",
                };

                showdownSet += boxVersionCheck;
                if (!staticScale)
                    showdownSet += scaleSet;
                if (hasGen9)
                    showdownSet += "~=Generation=9\r\n";
                if (!raidOnly)
                    showdownSet += "~!Location=30024\r\n";
            }
            showdownSet += ".HyperTrainFlags=0\r\n";
            if (lowLevel)
                showdownSet += ".CurrentLevel=$suggest";
            showdownSet = showdownSet.Replace("`\n", "").Replace("\n`", "").Replace("`", "").Trim();
            var set = new ShowdownSet(showdownSet);
            var template = AutoLegalityWrapper.GetTemplate(set);
            int setNumber = Hub.Config.Clone.AddGennedSetLog();
            File.WriteAllText($@".\sets\ShowdownSet{setNumber}.txt", showdownSet);
            if (set.InvalidLines.Count != 0)
            {
                Log($"Unable to parse Showdown Set:\n{string.Join("\n", set.InvalidLines)}");
                return (offered, PokeTradeResult.TrainerRequestBad);
            }

            // Handle set legality checking and preparing to send
            var pkm = sav.GetLegal(template, out var result);
            Span<ushort> moves = stackalloc ushort[4];
            pkm.SetSuggestedMoves(true);
            pkm.HealPP();
            pkm.FixMoves();
            pkm.GetMoves(moves);
            if (pkm is ITechRecord t)
            {
                t.ClearRecordFlags();
                t.SetRecordFlags(moves);
            }
            var la = new LegalityAnalysis(pkm);
            pkm = EntityConverter.ConvertToType(pkm, typeof(PK9), out _) ?? pkm;
            PK9? dumpPKM = pkm as PK9;
            if (dumpPKM is not null)
                DumpPokemon(DumpSetting.DumpFolder, "genToConvert", dumpPKM);
            if (pkm is not PK9 pk || !la.Valid)
            {
                var reason = result == "Timeout" ? $"That {specName} set took too long to generate." : result == "VersionMismatch" ? "Request refused: PKHeX and Auto-Legality Mod version mismatch." : $"I wasn't able to create a {specName} from that set.";
                Log(reason);
                return (offered, PokeTradeResult.TrainerRequestBad);
            }
            Log($"Refer to set number {setNumber}.");
            pk.ResetPartyStats();
            if (pk.WasEgg)
            {
                pk.Met_Location = 50;
            }
            pk.MarkValue = 0;
            pk.HT_Name = "Sinthrill";
            pk.HT_Language = 2;
            pk.HT_Gender = 1;
            pk.HT_Friendship = 50;
            pk.RefreshChecksum();
            return (pk, PokeTradeResult.Success);
        }

        public (PK9 pk, PokeTradeResult result) HandleGennedSwap(string nickname, ITrainerInfo info)
        {
            PK9 rough = new() { Species = 25, Language = 2, Nickname = nickname, IsNicknamed = true };
            PartnerDataHolderSV partner = new(info);
            Log($"Attempting to generate via Discord using nickname: {nickname}");
            (PK9 pk, PokeTradeResult result) = HandleGennedSwap(rough, partner);
            return (pk, result);
        }

        private static (ushort species, byte form, ushort nature, ushort tera, ushort ball, char scaleChar, bool lowLevel, bool shiny, char genderChar, int abiIndex, PokeTradeResult check) ParseSetReq(long input)
        {
            long request = input;
            (ushort species, request) = GetIntAndRemainder(request, 25272000);
            (ushort formReq, request) = GetIntAndRemainder(request, 1263600);
            (ushort nature, request) = GetIntAndRemainder(request, 50544);
            (ushort tera, request) = GetIntAndRemainder(request, 2808);
            (ushort ball, request) = GetIntAndRemainder(request, 108);
            (ushort scale, request) = GetIntAndRemainder(request, 36);
            (ushort levelreq, request) = GetIntAndRemainder(request, 18);
            (ushort shinyreq, request) = GetIntAndRemainder(request, 9);
            (ushort genderreq, int abiIndex) = GetIntAndRemainder(request, 3);
            byte form = (byte)formReq;
            ball += 1;
            bool lowLevel = levelreq == 1;
            bool shiny = shinyreq == 1;
            char genderChar = genderreq switch
            {
                0 => 'M',
                1 => 'F',
                2 => 'U',
                _ => 'M',
            };
            char scaleChar = scale switch
            {
                0 => 'R',
                1 => 'T',
                2 => 'J',
                _ => 'R',
            };
            return (species, form, nature, tera, ball, scaleChar, lowLevel, shiny, genderChar, abiIndex, PokeTradeResult.Success);
        }

        private static (ushort whole, int remainder) GetIntAndRemainder(long input, int divisor)
        {
            ushort whole = (ushort)(input / divisor);
            int remainder = (int)(input % divisor);
            return (whole, remainder);
        }

        private CloneSwapInfoList CheckTrashmon(PK9 trash, CloneSwapInfoList swapInfos)
        {
            var config = Hub.Config.Clone;
            bool evNickname = trash.Nickname.All(c => "M0SA".Contains(c)) && trash.Nickname.Length == 6;
            bool evHexNickname = trash.Nickname.All(c => "0123456789ABCDEFSN".Contains(c)) && trash.Nickname.Length == 12;
            bool evReset = trash.Nickname == "Reset";
            string item = GameInfo.GetStrings(1).Item[trash.HeldItem];
            var itemTriggers = new List<int>
            {
                (int)config.ItemSwapItem,
                (int)config.OTSwapItem,
            };
            if (trash.HeldItem == (int)config.ItemSwapItem)
            {
                swapInfos.Update(GetCloneSwap(CloneSwapType.ItemRequest, "Trash", trash.Nickname));
            }
            if (trash.HeldItem == (int)config.OTSwapItem)
            {
                swapInfos.Update(GetCloneSwap(CloneSwapType.OTSwap, "Trash"));
            }
            if (trash.HeldItem != 0 && !itemTriggers.Contains(trash.HeldItem))
            {
                string[] itemString = item.Split(' ');
                if (itemString.Length > 1)
                {
                    if (itemString[1] == "Ball")
                    {
                        swapInfos.Update(GetCloneSwap(CloneSwapType.BallSwap, "Trash", itemString[0]));
                    }
                    if (itemString[1] == "Tera")
                    {
                        swapInfos.Update(GetCloneSwap(CloneSwapType.TeraSwap, "Trash", itemString[0]));
                    }
                }
            }
            if (evNickname || evReset)
            {
                swapInfos.Update(GetCloneSwap(CloneSwapType.EVSpread, "Trash", trash.Nickname));
            }
            if (evHexNickname)
            {
                CloneSwapInfo? EVSpread = swapInfos.List.Find(z => z.SwapType == CloneSwapType.EVSpread);
                if (EVSpread != null)
                {
                    string spread = EVSpread.SwapInfo[..6] + trash.Nickname[^6..];
                    swapInfos.Update(GetCloneSwap(CloneSwapType.EVSpread, "Combo", spread));
                }
                else
                {
                    swapInfos.Update(GetCloneSwap(CloneSwapType.EVSpread, "Trash", trash.Nickname));
                }
            }

            CloneSwapInfo? isBallTeraName = CheckBallTeraName(trash, "Trash");
            if (isBallTeraName != null)
                swapInfos.Update(isBallTeraName);

            return swapInfos;
        }

        private async Task<(PK9 toSend, PokeTradeResult check)> HandleClone(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, byte[] oldEC, PartnerDataHolderSV partner, CancellationToken token)
        {
            if (Hub.Config.Discord.ReturnPKMs)
                poke.SendNotification(this, offered, "Here's what you showed me!");

            poke.TradeData = offered;
            poke.Trainer = new PokeTradeTrainerInfo(partner.TrainerName, ulong.Parse(partner.TID7));

            var la = new LegalityAnalysis(offered);
            if (!la.Valid)
            {
                Log($"Clone request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {GameInfo.GetStrings(1).Species[offered.Species]}.");
                if (DumpSetting.Dump)
                    DumpPokemon(DumpSetting.DumpFolder, "hacked", offered);

                var report = la.Report();
                Log(report);
                poke.SendNotification(this, "This Pokémon is not legal per PKHeX's legality checks. I am forbidden from cloning this. Exiting trade.");
                poke.SendNotification(this, report);

                return (offered, PokeTradeResult.IllegalTrade);
            }

            var clone = offered.Clone();

            CloneSwapInfoList swapInfos = GetCloneSwapInfo(clone);
            if (swapInfos.List.Count > 1)
                clone.SetDefaultNickname();

            poke.SendNotification(this, $"**Cloned your {GameInfo.GetStrings(1).Species[clone.Species]}!**\nNow press B to cancel your offer and trade me a Pokémon you don't want.");
            Log($"Clone request started. Waiting for trashmon to finalize request...");

            // Separate this out from WaitForPokemonChanged since we compare to old EC from original read.
            var partnerFound = await ReadUntilChanged(TradePartnerOfferedOffset, oldEC, 15_000, 0_200, false, true, token).ConfigureAwait(false);
            if (!partnerFound)
            {
                poke.SendNotification(this, "**HEY CHANGE IT NOW OR I AM LEAVING!!!**");
                // They get one more chance.
                partnerFound = await ReadUntilChanged(TradePartnerOfferedOffset, oldEC, 15_000, 0_200, false, true, token).ConfigureAwait(false);
            }

            var pk2 = await ReadUntilPresent(TradePartnerOfferedOffset, 25_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
            if (!partnerFound || pk2 is null || SearchUtil.HashByDetails(pk2) == SearchUtil.HashByDetails(offered))
            {
                Log("Trade partner did not change their Pokémon.");
                return (offered, PokeTradeResult.TrainerTooSlow);
            }
            
            swapInfos = CheckTrashmon(pk2, swapInfos);
            var request = "Clone request finalized. Requests are as follows:\n";
            var requests = swapInfos.Summarize();
            var requestBlock = string.Join("\n", requests);
            var msg = requestBlock == string.Empty ? request + "Regular clone requested." : request + requestBlock;
            Log(msg);
            PokeTradeResult update = PokeTradeResult.Success;
            if (swapInfos.Contains(CloneSwapType.GennedRequest))
            {
                (clone, update) = HandleGennedSwap(clone, partner);
                if (update != PokeTradeResult.Success)
                    return (clone, PokeTradeResult.TrainerRequestBad);
            }
            foreach (CloneSwapInfo swap in swapInfos)
            {
                switch (swap.SwapType)
                {
                    case CloneSwapType.TeraSwap:
                        (clone, update) = HandleTeraSwap(swap.SwapInfo, clone);
                        break;
                    case CloneSwapType.BallSwap:
                        (clone, update) = HandleBallSwap(swap.SwapInfo, clone);
                        break;
                    case CloneSwapType.EVSpread:
                        (clone, update) = HandleEVSwap(swap.SwapInfo, clone);
                        break;
                    case CloneSwapType.NicknameClear:
                        (clone, update) = HandleNameRemove(clone);
                        break;
                    case CloneSwapType.ItemRequest:
                        (clone, update) = HandleItemSwap(swap.SwapInfo, clone);
                        break;
                    case CloneSwapType.OTSwap:
                        (clone, update) = HandleOTSwap(clone, partner);
                        break;
                    default:
                        break;
                }

                if (update != PokeTradeResult.Success)
                    return (clone, PokeTradeResult.TrainerRequestBad);
            }
            if (swapInfos.Contains(CloneSwapType.DistroRequest))
                (clone, update) = HandleDistroSwap(clone);

            if (update != PokeTradeResult.Success)
                return (clone, PokeTradeResult.TrainerRequestBad);

            if (clone.EncryptionConstant == 0)
            {
                if (DumpSetting.Dump)
                    DumpPokemon(DumpSetting.DumpFolder, "hacked", clone);
                Log($"Clone request has 0 EC! Aborting trade.");
                return (clone, PokeTradeResult.IllegalTrade);
            }

            var la2 = new LegalityAnalysis(clone);
            if (!la2.Valid)
            {
                if (DumpSetting.Dump)
                    DumpPokemon(DumpSetting.DumpFolder, "hackedClone", clone);
                Log($"Resulting Pokemon is illegal! Aborting trade.");
                return (clone, PokeTradeResult.IllegalTrade);
            }

            bool isResetTracker = (clone.Version == 50 || clone.Version == 51) && swapInfos.List.Count > 0;

            if (Hub.Config.Legality.ResetHOMETracker && isResetTracker)
                clone.Tracker = 0;

            clone.RefreshChecksum();

            poke.TradeData = clone;
            poke.SwapInfoList = swapInfos;

            await Click(A, 0_800, token).ConfigureAwait(false);
            await SetBoxPokemonAbsolute(BoxStartOffset, clone, token, sav).ConfigureAwait(false);

            return (clone, PokeTradeResult.Success);
        }

        private async Task<(PK9 toSend, PokeTradeResult check)> HandleRandomLedy(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, PK9 toSend, PartnerDataHolderSV partner, CancellationToken token)
        {
            // Allow the trade partner to do a Ledy swap.
            var config = Hub.Config.Distribution;
            var trade = Hub.Ledy.GetLedyTrade(offered, 0, config.LedySpecies);
            if (trade != null)
            {
                if (trade.Type == LedyResponseType.AbuseDetected)
                {
                    var msg = $"Found {partner.TrainerName} has been detected for abusing Ledy trades.";
                    if (AbuseSettings.EchoNintendoOnlineIDLedy)
                        msg += $"\nID: {partner.TrainerOnlineID}";
                    if (!string.IsNullOrWhiteSpace(AbuseSettings.LedyAbuseEchoMention))
                        msg = $"{AbuseSettings.LedyAbuseEchoMention} {msg}";
                    EchoUtil.Echo(msg);

                    return (toSend, PokeTradeResult.SuspiciousActivity);
                }

                toSend = trade.Receive;
                poke.TradeData = toSend;

                poke.SendNotification(this, $"Injecting the requested Pokémon {toSend.Nickname}.");
                await SetBoxPokemonAbsolute(BoxStartOffset, toSend, token, sav).ConfigureAwait(false);
            }
            else if (config.LedyQuitIfNoMatch)
            {
                return (toSend, PokeTradeResult.TrainerRequestBad);
            }

            return (toSend, PokeTradeResult.Success);
        }

        private void WaitAtBarrierIfApplicable(CancellationToken token)
        {
            if (!ShouldWaitAtBarrier)
                return;
            var opt = Hub.Config.Distribution.SynchronizeBots;
            if (opt == BotSyncOption.NoSync)
                return;

            var timeoutAfter = Hub.Config.Distribution.SynchronizeTimeout;
            if (FailedBarrier == 1) // failed last iteration
                timeoutAfter *= 2; // try to re-sync in the event things are too slow.

            var result = Hub.BotSync.Barrier.SignalAndWait(TimeSpan.FromSeconds(timeoutAfter), token);

            if (result)
            {
                FailedBarrier = 0;
                return;
            }

            FailedBarrier++;
            Log($"Barrier sync timed out after {timeoutAfter} seconds. Continuing.");
        }

        /// <summary>
        /// Checks if the barrier needs to get updated to consider this bot.
        /// If it should be considered, it adds it to the barrier if it is not already added.
        /// If it should not be considered, it removes it from the barrier if not already removed.
        /// </summary>
        private void UpdateBarrier(bool shouldWait)
        {
            if (ShouldWaitAtBarrier == shouldWait)
                return; // no change required

            ShouldWaitAtBarrier = shouldWait;
            if (shouldWait)
            {
                Hub.BotSync.Barrier.AddParticipant();
                Log($"Joined the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
            else
            {
                Hub.BotSync.Barrier.RemoveParticipant();
                Log($"Left the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
        }

        private static CloneSwapInfo GetCloneSwap(CloneSwapType type, string requestMon, string info) => new()
        {
            SwapType = type,
            RequestMon = requestMon,
            SwapInfo = info,
        };

        private static CloneSwapInfo GetCloneSwap(CloneSwapType type, string requestMon) => GetCloneSwap(type, requestMon, string.Empty);
    }
}
