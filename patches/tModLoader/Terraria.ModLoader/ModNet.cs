﻿using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader.IO;

namespace Terraria.ModLoader
{
	public class ModNet
	{
		private class ModHeader
		{
			public string name;
			public Version version;
			public byte[] hash;
			public bool signed;
			public string path;

			public ModHeader(string name, Version version, byte[] hash, bool signed) {
				this.name = name;
				this.version = version;
				this.hash = hash;
				this.signed = signed;
				path = Path.Combine(ModLoader.ModPath, name + ".tmod");
			}

			public bool Matches(TmodFile mod) => name == mod.name && version == mod.version && hash.SequenceEqual(mod.hash);
			public override string ToString() => name + " v" + version;
		}

		public static bool AllowVanillaClients { get; internal set; }
		internal static bool downloadModsFromServers = true;
		internal static bool onlyDownloadSignedMods = false;

		internal static bool[] isModdedClient = new bool[256];

		private static Mod[] netMods;

		public static bool IsModdedClient(int i) => isModdedClient[i];

		public static Mod GetMod(int netID) =>
			netID >= 0 && netID < netMods.Length ? netMods[netID] : null;

		public static int NetModCount => netMods.Length;

		private static Queue<ModHeader> downloadQueue = new Queue<ModHeader>();
		private static ModHeader downloadingMod;
		private static FileStream downloadingFile;
		private static long downloadingLength;

		internal static void AssignNetIDs() {
			netMods = ModLoader.Mods.Where(mod => mod.Side != ModSide.Server).ToArray();
			for (short i = 0; i < netMods.Length; i++)
				netMods[i].netID = i;
		}

		internal static void Unload() {
			netMods = null;
			if (!Main.dedServ && Main.netMode != 1) //disable vanilla client compatiblity restrictions when reloading on a client
				AllowVanillaClients = false;
		}

		internal static void SyncMods(int clientIndex) {
			var p = new ModPacket(MessageID.SyncMods);
			p.Write(AllowVanillaClients);

			var syncMods = ModLoader.Mods.Where(mod => mod.Side == ModSide.Both).ToList();
			AddNoSyncDeps(syncMods);

			p.Write(syncMods.Count);
			foreach (var mod in syncMods) {
				p.Write(mod.Name);
				p.Write(mod.Version.ToString());
				p.Write(mod.File.hash);
				p.Write(mod.File.ValidModBrowserSignature);
			}

			p.Send(clientIndex);
		}

		private static void AddNoSyncDeps(List<Mod> syncMods) {
			var queue = new Queue<Mod>(syncMods.Where(m => m.Side == ModSide.Both));
			while (queue.Count > 0) {
				var depNames = ModOrganizer.dependencyCache[queue.Dequeue().Name];
				foreach (var dep in depNames.Select(ModLoader.GetMod).Where(m => m.Side == ModSide.NoSync && !syncMods.Contains(m))) {
					syncMods.Add(dep);
					queue.Enqueue(dep);
				}
			}
		}

		internal static void SyncClientMods(BinaryReader reader) {
			SyncClientMods(reader, out var needsReload);
			if (downloadQueue.Count > 0)
				DownloadNextMod();
			else
				OnModsDownloaded(needsReload);
		}

		// This method is split so that the local variables aren't held by the GC when reloading
		internal static void SyncClientMods(BinaryReader reader, out bool needsReload) {
			AllowVanillaClients = reader.ReadBoolean();
			Logging.tML.Info($"Server reports AllowVanillaClients set to {AllowVanillaClients}");

			Main.statusText = Language.GetTextValue("tModLoader.MPSyncingMods");
			var clientMods = ModLoader.Mods;
			var modFiles = ModOrganizer.FindMods();
			needsReload = false;
			downloadQueue.Clear();
			var syncSet = new HashSet<string>();
			var blockedList = new List<ModHeader>();

			int n = reader.ReadInt32();
			for (int i = 0; i < n; i++) {
				var header = new ModHeader(reader.ReadString(), new Version(reader.ReadString()), reader.ReadBytes(20), reader.ReadBoolean());
				syncSet.Add(header.name);

				var clientMod = clientMods.SingleOrDefault(m => m.Name == header.name);
				if (clientMod != null) {
					if (header.Matches(clientMod.File))
						continue;

					header.path = clientMod.File.path;
				}
				else {
					var disabledVersions = modFiles.Where(m => m.Name == header.name).ToArray();
					var matching = disabledVersions.FirstOrDefault(mod => header.Matches(mod.modFile));
					if (matching != null) {
						matching.Enabled = true;
						needsReload = true;
						continue;
					}

					if (disabledVersions.Length > 0)
						header.path = disabledVersions[0].modFile.path;
				}

				if (downloadModsFromServers && (header.signed || !onlyDownloadSignedMods))
					downloadQueue.Enqueue(header);
				else
					blockedList.Add(header);
			}

			foreach (var mod in clientMods)
				if (mod.Side == ModSide.Both && !syncSet.Contains(mod.Name)) {
					ModLoader.DisableMod(mod.Name);
					needsReload = true;
				}

			if (blockedList.Count > 0) {
				var msg = Language.GetTextValue("tModLoader.MPServerModsCantDownload");
				msg += downloadModsFromServers
					? Language.GetTextValue("tModLoader.MPServerModsCantDownloadReasonSigned")
					: Language.GetTextValue("tModLoader.MPServerModsCantDownloadReasonAutomaticDownloadDisabled");
				msg += ".\n" + Language.GetTextValue("tModLoader.MPServerModsCantDownloadChangeSettingsHint") + "\n";
				foreach (var mod in blockedList)
					msg += "\n    " + mod;

				Logging.tML.Warn(msg);
				Interface.errorMessage.Show(msg, 0);
			}
		}

		private static void DownloadNextMod() {
			downloadingMod = downloadQueue.Dequeue();
			downloadingFile = null;

			var p = new ModPacket(MessageID.ModFile);
			p.Write(downloadingMod.name);
			p.Send();
		}

		private const int CHUNK_SIZE = 16384;
		internal static void SendMod(string modName, int toClient) {
			var mod = ModLoader.GetMod(modName);
			var path = mod.File.path;
			var fs = File.OpenRead(path);
			{//send file length
				var p = new ModPacket(MessageID.ModFile);
				p.Write(mod.DisplayName);
				p.Write(fs.Length);
				p.Send(toClient);
			}

			var buf = new byte[CHUNK_SIZE];
			int count;
			while ((count = fs.Read(buf, 0, buf.Length)) > 0) {
				var p = new ModPacket(MessageID.ModFile, CHUNK_SIZE + 3);
				p.Write(buf, 0, count);
				p.Send(toClient);
			}

			fs.Close();
		}

		internal static void ReceiveMod(BinaryReader reader) {
			if (downloadingMod == null)
				return;

			try {
				if (downloadingFile == null) {
					Interface.downloadMod.SetDownloading(reader.ReadString());
					Interface.downloadMod.SetCancel(CancelDownload);
					Main.menuMode = Interface.downloadModID;

					ModLoader.GetMod(downloadingMod.name)?.File?.Close();
					downloadingLength = reader.ReadInt64();
					downloadingFile = new FileStream(downloadingMod.path, FileMode.Create);
					return;
				}

				var bytes = reader.ReadBytes((int)Math.Min(downloadingLength - downloadingFile.Position, CHUNK_SIZE));
				downloadingFile.Write(bytes, 0, bytes.Length);
				Interface.downloadMod.SetProgress(downloadingFile.Position, downloadingLength);

				if (downloadingFile.Position == downloadingLength) {
					downloadingFile.Close();
					var mod = new TmodFile(downloadingMod.path);
					mod.Read();
					mod.Close();

					if (!downloadingMod.Matches(mod))
						throw new Exception(Language.GetTextValue("tModLoader.MPErrorModHashMismatch"));

					if (downloadingMod.signed && !mod.ValidModBrowserSignature)
						throw new Exception(Language.GetTextValue("tModLoader.MPErrorModNotSigned"));

					ModLoader.EnableMod(mod.name);

					if (downloadQueue.Count > 0)
						DownloadNextMod();
					else
						OnModsDownloaded(true);
				}
			}
			catch (Exception e) {
				try {
					downloadingFile?.Close();
					File.Delete(downloadingMod.path);
				}
				catch { }
				
				var msg = Language.GetTextValue("tModLoader.MPErrorModDownloadError", downloadingMod.name);
				Logging.tML.Error(msg, e);
				Interface.errorMessage.Show(msg + e, 0);
				
				Netplay.disconnect = true;
				downloadingMod = null;
			}
		}

		private static void CancelDownload() {
			try {
				downloadingFile?.Close();
				File.Delete(downloadingMod.path);
			}
			catch { }
			downloadingMod = null;
			Netplay.disconnect = true;
			Main.menuMode = 0;
		}

		private static void OnModsDownloaded(bool needsReload) {
			if (needsReload) {
				ModLoader.OnSuccessfulLoad = NetReload();
				ModLoader.Reload();
				return;
			}

			downloadingMod = null;
			netMods = null;
			foreach (var mod in ModLoader.Mods)
				mod.netID = -1;

			new ModPacket(MessageID.SyncMods).Send();
		}

		private static Action NetReload() {
			// Main.ActivePlayerFileData gets cleared during reload
			var path = Main.ActivePlayerFileData.Path;
			var isCloudSave = Main.ActivePlayerFileData.IsCloudSave;
			return () => {
				// re-select the current player
				Player.GetFileData(path, isCloudSave).SetAsActive();
				//from Netplay.ClientLoopSetup
				Main.player[Main.myPlayer].hostile = false;
				Main.clientPlayer = (Player)Main.player[Main.myPlayer].clientClone();

				Main.menuMode = 10;
				OnModsDownloaded(false);
			};
		}

		internal static void SendNetIDs(int toClient) {
			var p = new ModPacket(MessageID.ModPacket);
			p.Write(netMods.Length);
			foreach (var mod in netMods)
				p.Write(mod.Name);

			ItemLoader.WriteNetGlobalOrder(p);
			WorldHooks.WriteNetWorldOrder(p);

			p.Send(toClient);
		}

		private static void ReadNetIDs(BinaryReader reader) {
			var mods = ModLoader.Mods;
			var list = new List<Mod>();
			var n = reader.ReadInt32();
			for (short i = 0; i < n; i++) {
				var name = reader.ReadString();
				var mod = mods.SingleOrDefault(m => m.Name == name);
				list.Add(mod);
				if (mod != null) //nosync mod that doesn't exist on the client
					mod.netID = i;
			}
			netMods = list.ToArray();
			SetupDiagnostics();

			ItemLoader.ReadNetGlobalOrder(reader);
			WorldHooks.ReadNetWorldOrder(reader);
		}

		internal static void HandleModPacket(BinaryReader reader, int whoAmI, int length) {
			if (netMods == null) {
				ReadNetIDs(reader);
				return;
			}

			var id = NetModCount < 256 ? reader.ReadByte() : reader.ReadInt16();
			GetMod(id)?.HandlePacket(reader, whoAmI);

			if (Main.netMode == 1) {
				rxMsgType[id]++;
				rxDataType[id] += length;
			}
		}

		internal static bool HijackGetData(ref byte messageType, ref BinaryReader reader, int playerNumber) {
			if (netMods == null) {
				return false;
			}

			bool hijacked = false;
			long readerPos = reader.BaseStream.Position;
			long biggestReaderPos = readerPos;
			foreach (var mod in ModLoader.Mods) {
				if (mod.HijackGetData(ref messageType, ref reader, playerNumber)) {
					hijacked = true;
					biggestReaderPos = Math.Max(reader.BaseStream.Position, biggestReaderPos);
				}
				reader.BaseStream.Position = readerPos;
			}
			if (hijacked) {
				reader.BaseStream.Position = biggestReaderPos;
			}
			return hijacked;
		}

		internal static bool HijackSendData(int whoAmI, int msgType, int remoteClient, int ignoreClient, NetworkText text, int number, float number2, float number3, float number4, int number5, int number6, int number7) {
			bool hijacked = false;
			foreach (Mod mod in ModLoader.Mods) {
				hijacked |= mod.HijackSendData(whoAmI, msgType, remoteClient, ignoreClient, text, number, number2, number3, number4, number5, number6, number7);
			}
			return hijacked;
		}

		// Mirror of Main class network diagnostic fields, but mod specific.
		// Potential improvements: separate page from vanilla messageIDs, track automatic/ModWorld/etc sends per class or mod, sort by most active, moving average, NetStats console command in ModLoaderMod
		// Currently we only update these on client
		public static int[] rxMsgType;
		public static int[] rxDataType;
		public static int[] txMsgType;
		public static int[] txDataType;

		private static void SetupDiagnostics() {
			rxMsgType = new int[netMods.Length];
			rxDataType = new int[netMods.Length];
			txMsgType = new int[netMods.Length];
			txDataType = new int[netMods.Length];
		}

		internal static void ResetNetDiag() {
			if (netMods == null || Main.netMode == 2) return;
			for (int i = 0; i < netMods.Length; i++) {
				rxMsgType[i] = 0;
				rxDataType[i] = 0;
				txMsgType[i] = 0;
				txDataType[i] = 0;
			}
		}

		internal static void DrawModDiagnoseNet() {
			if (netMods == null) return;
			float scale = 0.7f;

			for (int j = -1; j < netMods.Length; j++) {
				int i = j + Main.maxMsg + 2;
				int x = 200;
				int y = 120;
				int xAdjust = i / 50;
				x += xAdjust * 400;
				y += (i - xAdjust * 50) * 13;
				if (j == -1) {
					Main.spriteBatch.DrawString(Main.fontMouseText, "Mod          Received(#, Bytes)     Sent(#, Bytes)", new Vector2((float)x, (float)y), Color.White, 0f, default(Vector2), scale, SpriteEffects.None, 0f);
					continue;
				}
				Main.spriteBatch.DrawString(Main.fontMouseText, netMods[j].Name, new Vector2(x, y), Color.White, 0f, default(Vector2), scale, SpriteEffects.None, 0f);
				x += 120;
				Main.spriteBatch.DrawString(Main.fontMouseText, rxMsgType[j].ToString(), new Vector2(x, y), Color.White, 0f, default(Vector2), scale, SpriteEffects.None, 0f);
				x += 30;
				Main.spriteBatch.DrawString(Main.fontMouseText, rxDataType[j].ToString(), new Vector2(x, y), Color.White, 0f, default(Vector2), scale, SpriteEffects.None, 0f);
				x += 80;
				Main.spriteBatch.DrawString(Main.fontMouseText, txMsgType[j].ToString(), new Vector2(x, y), Color.White, 0f, default(Vector2), scale, SpriteEffects.None, 0f);
				x += 30;
				Main.spriteBatch.DrawString(Main.fontMouseText, txDataType[j].ToString(), new Vector2(x, y), Color.White, 0f, default(Vector2), scale, SpriteEffects.None, 0f);
			}
		}
	}
}