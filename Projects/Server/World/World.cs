/*************************************************************************
 * ModernUO                                                              *
 * Copyright 2019-2020 - ModernUO Development Team                       *
 * Email: hi@modernuo.com                                                *
 * File: World.cs                                                        *
 *                                                                       *
 * This program is free software: you can redistribute it and/or modify  *
 * it under the terms of the GNU General Public License as published by  *
 * the Free Software Foundation, either version 3 of the License, or     *
 * (at your option) any later version.                                   *
 *                                                                       *
 * You should have received a copy of the GNU General Public License     *
 * along with this program.  If not, see <http://www.gnu.org/licenses/>. *
 *************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Server.Guilds;
using Server.Network;

namespace Server
{
    public enum WorldState
    {
        Initial,
        Loading,
        Running,
        Saving,
        WritingSave
    }

    public static class World
    {
        private static readonly ManualResetEvent m_DiskWriteHandle = new(true);
        private static readonly Dictionary<Serial, IEntity> _pendingAdd = new();
        private static readonly Dictionary<Serial, IEntity> _pendingDelete = new();
        private static readonly ConcurrentQueue<Item> _decayQueue = new();

        private static string _tempSavePath; // Path to the temporary folder for the save
        private static string _savePath; // Path to "Saves" folder

        public const uint ItemOffset = 0x40000000;
        public const uint MaxItemSerial = 0x7FFFFFFF;
        public const uint MaxMobileSerial = ItemOffset - 1;
        private const uint _maxItems = MaxItemSerial - ItemOffset + 1;

        private static Serial _lastMobile = Serial.Zero;
        private static Serial _lastItem = ItemOffset;
        private static Serial _lastGuild = Serial.Zero;

        public static Serial NewMobile
        {
            get
            {
                uint last = _lastMobile;

                for (int i = 0; i < MaxMobileSerial; i++)
                {
                    last++;

                    if (last > MaxMobileSerial)
                    {
                        last = 1;
                    }

                    if (FindMobile(last) == null)
                    {
                        return _lastMobile = last;
                    }
                }

                OutOfMemory("No serials left to allocate for mobiles");
                return Serial.MinusOne;
            }
        }

        public static Serial NewItem
        {
            get
            {
                uint last = _lastItem;

                for (int i = 0; i < _maxItems; i++)
                {
                    last++;

                    if (last > MaxItemSerial)
                    {
                        last = ItemOffset;
                    }

                    if (FindItem(last) == null)
                    {
                        return _lastItem = last;
                    }
                }

                OutOfMemory("No serials left to allocate for items");
                return Serial.MinusOne;
            }
        }

        public static Serial NewGuild
        {
            get
            {
                while (FindGuild(_lastGuild += 1) != null)
                {
                }

                return _lastGuild;
            }
        }

        private static void OutOfMemory(string message) => throw new OutOfMemoryException(message);

        internal static List<Type> ItemTypes { get; } = new();
        internal static List<Type> MobileTypes { get; } = new();
        internal static List<Type> GuildTypes { get; } = new();

        public static WorldState WorldState { get; private set; }
        public static bool Saving => WorldState == WorldState.Saving;
        public static bool Running => WorldState != WorldState.Loading && WorldState != WorldState.Initial;
        public static bool Loading => WorldState == WorldState.Loading;

        public static Dictionary<Serial, Mobile> Mobiles { get; private set; }
        public static Dictionary<Serial, Item> Items { get; private set; }
        public static Dictionary<Serial, BaseGuild> Guilds { get; private set; }

        public static void Configure()
        {
            var tempSavePath = ServerConfiguration.GetOrUpdateSetting("world.tempSavePath", "temp");
            _tempSavePath = Path.Combine(Core.BaseDirectory, tempSavePath);
            var savePath = ServerConfiguration.GetOrUpdateSetting("world.savePath", "Saves");
            _savePath = Path.Combine(Core.BaseDirectory, savePath);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WaitForWriteCompletion()
        {
            m_DiskWriteHandle.WaitOne();
        }

        private static void EnqueueForDecay(Item item)
        {
            if (WorldState != WorldState.Saving)
            {
                WriteConsoleLine($"Attempting to queue {item} for decay but the world is not saving");
                return;
            }

            _decayQueue.Enqueue(item);
        }

        public static void Broadcast(int hue, bool ascii, string text)
        {
            var length = OutgoingMessagePackets.GetMaxMessageLength(text);

            Span<byte> buffer = stackalloc byte[length].InitializePacket();

            foreach (var ns in TcpServer.Instances)
            {
                if (ns.Mobile == null)
                {
                    continue;
                }

                length = OutgoingMessagePackets.CreateMessage(
                    buffer, Serial.MinusOne, -1, MessageType.Regular, hue, 3, ascii, "ENU", "System", text
                );

                if (length != buffer.Length)
                {
                    buffer = buffer.SliceToLength(length); // Adjust to the actual size
                }

                ns.Send(buffer);
            }

            NetState.FlushAll();
        }

        public static void BroadcastStaff(int hue, bool ascii, string text)
        {
            var length = OutgoingMessagePackets.GetMaxMessageLength(text);

            Span<byte> buffer = stackalloc byte[length].InitializePacket();

            foreach (var ns in TcpServer.Instances)
            {
                if (ns.Mobile == null || ns.Mobile.AccessLevel < AccessLevel.GameMaster)
                {
                    continue;
                }

                length = OutgoingMessagePackets.CreateMessage(
                    buffer, Serial.MinusOne, -1, MessageType.Regular, hue, 3, ascii, "ENU", "System", text
                );

                if (length != buffer.Length)
                {
                    buffer = buffer.SliceToLength(length); // Adjust to the actual size
                }

                ns.Send(buffer);
            }

            NetState.FlushAll();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Broadcast(int hue, bool ascii, string format, params object[] args) =>
            Broadcast(hue, ascii, string.Format(format, args));

        private static List<Tuple<ConstructorInfo, string>> ReadTypes<I>(BinaryReader tdbReader)
        {
            var constructorTypes = new[] { typeof(I) };

            var count = tdbReader.ReadInt32();

            var types = new List<Tuple<ConstructorInfo, string>>(count);

            for (var i = 0; i < count; ++i)
            {
                var typeName = tdbReader.ReadString();

                var t = AssemblyHandler.FindTypeByFullName(typeName, false);

                if (t?.IsAbstract != false)
                {
                    WriteConsoleLine("failed");

                    var issue = t?.IsAbstract == true ? "marked abstract" : "not found";

                    WriteConsoleLine($"Error: Type '{typeName}' was {issue}. Delete all of those types? (y/n)");

                    if (Console.ReadKey(true).Key == ConsoleKey.Y)
                    {
                        types.Add(null);
                        WriteConsole("Loading...");
                        continue;
                    }

                    WriteConsoleLine("Types will not be deleted. An exception will be thrown.");

                    throw new Exception($"Bad type '{typeName}'");
                }

                var ctor = t.GetConstructor(constructorTypes);

                if (ctor != null)
                {
                    types.Add(new Tuple<ConstructorInfo, string>(ctor, typeName));
                }
                else
                {
                    throw new Exception($"Type '{t}' does not have a serialization constructor");
                }
            }

            return types;
        }

        private static Dictionary<I, T> LoadIndex<I, T>(IIndexInfo<I> indexInfo, out List<EntityIndex<T>> entities) where T : class, ISerializable
        {
            var map = new Dictionary<I, T>();
            object[] ctorArgs = new object[1];

            var indexType = indexInfo.TypeName;

            string indexPath = Path.Combine(_savePath, indexType, $"{indexType}.idx");
            string typesPath = Path.Combine(_savePath, indexType, $"{indexType}.tdb");

            entities = new List<EntityIndex<T>>();

            if (!File.Exists(indexPath) || !File.Exists(typesPath))
            {
                return map;
            }

            using FileStream idx = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            BinaryReader idxReader = new BinaryReader(idx);

            using FileStream tdb = new FileStream(typesPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            BinaryReader tdbReader = new BinaryReader(tdb);

            List<Tuple<ConstructorInfo, string>> types = ReadTypes<I>(tdbReader);

            var count = idxReader.ReadInt32();

            for (int i = 0; i < count; ++i)
            {
                var typeID = idxReader.ReadInt32();
                var number = idxReader.ReadUInt32();
                var pos = idxReader.ReadInt64();
                var length = idxReader.ReadInt32();

                Tuple<ConstructorInfo, string> objs = types[typeID];

                if (objs == null)
                {
                    continue;
                }

                T t;
                ConstructorInfo ctor = objs.Item1;
                I indexer = indexInfo.CreateIndex(number);

                ctorArgs[0] = indexer;
                t = ctor.Invoke(ctorArgs) as T;

                if (t != null)
                {
                    entities.Add(new EntityIndex<T>(t, typeID, pos, length));
                    map[indexer] = t;
                }
            }

            tdbReader.Close();
            idxReader.Close();

            return map;
        }

        private static void LoadData<I, T>(IIndexInfo<I> indexInfo, List<EntityIndex<T>> entities) where T : class, ISerializable
        {
            var indexType = indexInfo.TypeName;

            string dataPath = Path.Combine(_savePath, indexType, $"{indexType}.bin");

            if (!File.Exists(dataPath))
            {
                return;
            }

            using FileStream bin = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            BufferReader br = null;

            foreach (var entry in entities)
            {
                T t = entry.Entity;

                // Skip this entry
                if (t == null)
                {
                    bin.Seek(entry.Length, SeekOrigin.Current);
                    continue;
                }

                var buffer = GC.AllocateUninitializedArray<byte>(entry.Length);
                if (br == null)
                {
                    br = new BufferReader(buffer);
                }
                else
                {
                    br.SwapBuffers(buffer, out _);
                }

                bin.Read(buffer.AsSpan());
                string error;

                try
                {
                    t.Deserialize(br);

                    error = br.Position != entry.Length
                        ? $"Serialized object was {entry.Length} bytes, but {br.Position} bytes deserialized"
                        : null;
                }
                catch (Exception e)
                {
                    error = e.ToString();
                }

                if (error == null)
                {
                    t.InitializeSaveBuffer(buffer);
                }
                else
                {
                    Utility.PushColor(ConsoleColor.Red);
                    WriteConsoleLine($"***** Bad deserialize of {t.GetType()} *****");
                    WriteConsoleLine(error);
                    Utility.PopColor();

                    WriteConsoleLine("Delete the object and continue? (y/n)");

                    if (Console.ReadKey(true).Key != ConsoleKey.Y)
                    {
                        throw new Exception("Deserialization failed.");
                    }
                    t.Delete();
                }
            }
        }

        public static void Load()
        {
            if (WorldState != WorldState.Initial)
            {
                return;
            }

            WorldState = WorldState.Loading;

            WriteConsole("Loading...");
            var watch = Stopwatch.StartNew();

            IIndexInfo<Serial> itemIndexInfo = new EntityTypeIndex("Items");
            IIndexInfo<Serial> mobileIndexInfo = new EntityTypeIndex("Mobiles");
            IIndexInfo<Serial> guildIndexInfo = new EntityTypeIndex("Guilds");

            Mobiles = LoadIndex(mobileIndexInfo, out List<EntityIndex<Mobile>> mobiles);
            Items = LoadIndex(itemIndexInfo, out List<EntityIndex<Item>> items);
            Guilds = LoadIndex(guildIndexInfo, out List<EntityIndex<BaseGuild>> guilds);

            LoadData(mobileIndexInfo, mobiles);
            LoadData(itemIndexInfo, items);
            LoadData(guildIndexInfo, guilds);

            EventSink.InvokeWorldLoad();

            ProcessSafetyQueues();

            foreach (var item in Items.Values)
            {
                if (item.Parent == null)
                {
                    item.UpdateTotals();
                }

                item.ClearProperties();
            }

            foreach (var m in Mobiles.Values)
            {
                m.UpdateRegion(); // Is this really needed?
                m.UpdateTotals();

                m.ClearProperties();
            }

            watch.Stop();

            Utility.PushColor(ConsoleColor.Green);
            Console.WriteLine(
                "done ({1} items, {2} mobiles) ({0:F2} seconds)",
                watch.Elapsed.TotalSeconds,
                Items.Count,
                Mobiles.Count
            );
            Utility.PopColor();

            WorldState = WorldState.Running;
        }

        private static void ProcessSafetyQueues()
        {
            foreach (var entity in _pendingAdd.Values)
            {
                AddEntity(entity);
            }

            foreach (var entity in _pendingDelete.Values)
            {
                if (_pendingAdd.ContainsKey(entity.Serial))
                {
                    Console.Error.WriteLine("Entity {0} was both pending both deletion and addition after save", entity);
                }

                RemoveEntity(entity);
            }
        }

        private static void AppendSafetyLog(string action, ISerializable entity)
        {
            var message =
                $"Warning: Attempted to {action} {entity} during world save.{Environment.NewLine}This action could cause inconsistent state.{Environment.NewLine}It is strongly advised that the offending scripts be corrected.";

            WriteConsoleLine(message);

            try
            {
                using var op = new StreamWriter("world-save-errors.log", true);
                op.WriteLine("{0}\t{1}", DateTime.UtcNow, message);
                op.WriteLine(new StackTrace(2).ToString());
                op.WriteLine();
            }
            catch
            {
                // ignored
            }
        }

        private static void FinishWorldSave()
        {
            WorldState = WorldState.Running;

            ProcessDecay();
            ProcessSafetyQueues();
        }

        private static void TraceException(Exception ex)
        {
            try
            {
                using var op = new StreamWriter("save-errors.log", true);
                op.WriteLine("# {0}", DateTime.UtcNow);

                op.WriteLine(ex);

                op.WriteLine();
                op.WriteLine();
            }
            catch
            {
                // ignored
            }

            Console.WriteLine(ex);
        }

        private static void TraceSave(params IEnumerable<KeyValuePair<string, int>>[] entityTypes)
        {
            try
            {
                int count = 0;

                var timestamp = Utility.GetTimeStamp();
                using var op = new StreamWriter("Logs/Saves/Save-Stats-{0}.log", true);

                for (var i = 0; i < entityTypes.Length; i++)
                {
                    foreach (var (t, c) in entityTypes[i])
                    {
                        op.WriteLine("{0}: {1}", t, c);
                        count++;
                    }
                }

                op.WriteLine("- Total: {0}", count);

                op.WriteLine();
                op.WriteLine();
            }
            catch
            {
                // ignored
            }
        }

        public static void WriteFiles(object state)
        {
            IIndexInfo<Serial> itemIndexInfo = new EntityTypeIndex("Items");
            IIndexInfo<Serial> mobileIndexInfo = new EntityTypeIndex("Mobiles");
            IIndexInfo<Serial> guildIndexInfo = new EntityTypeIndex("Guilds");

            Exception exception = null;

            Dictionary<string, int> mobileCounts = null;
            Dictionary<string, int> itemCounts = null;
            Dictionary<string, int> guildCounts = null;

            var tempPath = Path.Combine(_tempSavePath, Utility.GetTimeStamp());

            try
            {
                var watch = Stopwatch.StartNew();
                WriteConsole("Writing snapshot...");

                WriteEntities(mobileIndexInfo, Mobiles, MobileTypes, tempPath, out mobileCounts);
                WriteEntities(itemIndexInfo, Items, ItemTypes, tempPath, out itemCounts);
                WriteEntities(guildIndexInfo, Guilds, GuildTypes, tempPath, out guildCounts);

                watch.Stop();

                Utility.PushColor(ConsoleColor.Green);
                Console.WriteLine("done ({0:F2} seconds)", watch.Elapsed.TotalSeconds);
                Utility.PopColor();
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            if (exception != null)
            {
                Utility.PushColor(ConsoleColor.Red);
                Console.WriteLine("failed");
                Utility.PopColor();
                TraceException(exception);

                BroadcastStaff(0x35, true, "Writing world save snapshot failed.");
            }
            else
            {
                try
                {
                    TraceSave(mobileCounts.ToList(), itemCounts.ToList(), guildCounts.ToList());

                    EventSink.InvokeWorldSavePostSnapshot(_savePath, tempPath);
                    Directory.Move(tempPath, _savePath);
                }
                catch (Exception ex)
                {
                    TraceException(ex);
                }
            }

            m_DiskWriteHandle.Set();

            Timer.DelayCall(FinishWorldSave);
        }

        private static void WriteEntities<I, T>(
            IIndexInfo<I> indexInfo,
            Dictionary<I, T> entities,
            List<Type> types,
            string savePath,
            out Dictionary<string, int> counts
        ) where T : class, ISerializable
        {
            counts = new Dictionary<string, int>();

            var typeName = indexInfo.TypeName;

            var path = Path.Combine(savePath, typeName);

            AssemblyHandler.EnsureDirectory(path);

            string idxPath = Path.Combine(path, $"{typeName}.idx");
            string tdbPath = Path.Combine(path, $"{typeName}.tdb");
            string binPath = Path.Combine(path, $"{typeName}.bin");

            using var idx = new BinaryFileWriter(idxPath, false);
            using var tdb = new BinaryFileWriter(tdbPath, false);
            using var bin = new BinaryFileWriter(binPath, true);

            idx.Write(entities.Count);
            foreach (var e in entities.Values)
            {
                long start = bin.Position;

                idx.Write(e.TypeRef);
                idx.Write(e.Serial);
                idx.Write(start);

                e.SerializeTo(bin);

                idx.Write((int)(bin.Position - start));

                var type = e.GetType().FullName;
                if (type != null)
                {
                    counts[type] = (counts.TryGetValue(type, out var count) ? count : 0) + 1;
                }
            }

            tdb.Write(types.Count);
            for (int i = 0; i < types.Count; ++i)
            {
                tdb.Write(types[i].FullName);
            }
        }

        private static void SaveEntities<T>(IEnumerable<T> list, DateTime serializeStart) where T : class, ISerializable
        {
            Parallel.ForEach(list, t => {
                if (t is Item item && item.CanDecay() && item.LastMoved + item.DecayTime <= serializeStart)
                {
                    EnqueueForDecay(item);
                }

                t.Serialize();
            });
        }

        private static void ProcessDecay()
        {
            while (_decayQueue.TryDequeue(out var item))
            {
                if (item.OnDecay())
                {
                    // TODO: Add Logging
                    item.Delete();
                }
            }
        }

        public static void Save()
        {
            if (WorldState != WorldState.Running)
            {
                return;
            }

            WaitForWriteCompletion(); // Blocks Save until current disk flush is done.

            WorldState = WorldState.Saving;

            m_DiskWriteHandle.Reset();

            Broadcast(0x35, true, "The world is saving, please wait.");

            var now = DateTime.UtcNow;

            WriteConsole("Saving...");

            var watch = Stopwatch.StartNew();

            Exception exception = null;

            try
            {
                SaveEntities(Items.Values, now);
                SaveEntities(Mobiles.Values, now);
                SaveEntities(Guilds.Values, now);

                EventSink.InvokeWorldSave();
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            WorldState = WorldState.WritingSave;

            watch.Stop();

            if (exception == null)
            {
                var duration = watch.Elapsed.TotalSeconds;
                Utility.PushColor(ConsoleColor.Green);
                Console.WriteLine("done ({0:F2} seconds)", duration);
                Utility.PopColor();

                // Only broadcast if it took at least 150ms
                if (duration >= 0.15)
                {
                    Broadcast(0x35, true, $"World Save completed in {duration:F2} seconds.");
                }
            }
            else
            {
                Utility.PushColor(ConsoleColor.Red);
                Console.WriteLine("failed");
                Utility.PopColor();
                TraceException(exception);

                BroadcastStaff(0x35, true, "World save failed.");
            }

            ThreadPool.QueueUserWorkItem(WriteFiles);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IEntity FindEntity(Serial serial, bool returnDeleted = false) => FindEntity<IEntity>(serial);

        public static T FindEntity<T>(Serial serial, bool returnDeleted = false) where T : class, IEntity
        {
            switch (WorldState)
            {
                default: return default;
                case WorldState.Loading:
                case WorldState.Saving:
                case WorldState.WritingSave:
                    {
                        if (_pendingDelete.TryGetValue(serial, out var entity))
                        {
                            return !returnDeleted ? null : entity as T;
                        }

                        if (_pendingAdd.TryGetValue(serial, out entity))
                        {
                            return entity as T;
                        }

                        goto case WorldState.Running;
                    }
                case WorldState.Running:
                    {
                        if (serial.IsItem)
                        {
                            Items.TryGetValue(serial, out var item);
                            return item as T;
                        }

                        if (serial.IsMobile)
                        {
                            Mobiles.TryGetValue(serial, out var mob);
                            return mob as T;
                        }

                        return default;
                    }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Item FindItem(Serial serial, bool returnDeleted = false) => FindEntity<Item>(serial, returnDeleted);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Mobile FindMobile(Serial serial, bool returnDeleted = false) =>
            FindEntity<Mobile>(serial, returnDeleted);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BaseGuild FindGuild(Serial serial) => Guilds.TryGetValue(serial, out var guild) ? guild : null;

        public static void AddEntity<T>(T entity) where T : class, IEntity
        {
            switch (WorldState)
            {
                default: // Not Running
                    {
                        throw new Exception($"Added {entity.GetType().Name} before world load.\n");
                    }
                case WorldState.Saving:
                    {
                        AppendSafetyLog("add", entity);
                        goto case WorldState.WritingSave;
                    }
                case WorldState.Loading:
                case WorldState.WritingSave:
                    {
                        if (_pendingDelete.Remove(entity.Serial))
                        {
                            Utility.PushColor(ConsoleColor.Red);
                            WriteConsoleLine($"Deleted then added {entity.GetType().Name} during {WorldState.ToString()} state.");
                            Utility.PopColor();
                        }
                        _pendingAdd[entity.Serial] = entity;
                        break;
                    }
                case WorldState.Running:
                    {
                        if (entity.Serial.IsItem)
                        {
                            Items[entity.Serial] = entity as Item;
                        }

                        if (entity.Serial.IsMobile)
                        {
                            Mobiles[entity.Serial] = entity as Mobile;
                        }
                        break;
                    }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddGuild(BaseGuild guild) => Guilds[guild.Serial] = guild;

        public static void RemoveEntity<T>(T entity) where T : class, IEntity
        {
            switch (WorldState)
            {
                default: // Not Running
                    {
                        throw new Exception($"Removed {entity.GetType().Name} before world load.\n");
                    }
                case WorldState.Saving:
                    {
                        AppendSafetyLog("delete", entity);
                        goto case WorldState.WritingSave;
                    }
                case WorldState.Loading:
                case WorldState.WritingSave:
                    {
                        _pendingAdd.Remove(entity.Serial);
                        _pendingDelete[entity.Serial] = entity;
                        break;
                    }
                case WorldState.Running:
                    {
                        if (entity.Serial.IsItem)
                        {
                            Items.Remove(entity.Serial);
                        }

                        if (entity.Serial.IsMobile)
                        {
                            Mobiles.Remove(entity.Serial);
                        }
                        break;
                    }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveGuild(BaseGuild guild) => Guilds.Remove(guild.Serial);

        private static void SerializeTo(this ISerializable entity, IGenericWriter writer)
        {
            var saveBuffer = entity.SaveBuffer;
            writer.Write(saveBuffer.Buffer.AsSpan(0, (int)saveBuffer.Position));

            // Resize to exact buffer size
            entity.SaveBuffer.Resize((int)entity.SaveBuffer.Position);
        }

        private static void WriteConsole(string message)
        {
            var now = DateTime.UtcNow;
            Console.Write("[{0} {1}] World: {2}", now.ToShortDateString(), now.ToLongTimeString(), message);
        }

        private static void WriteConsoleLine(string message)
        {
            var now = DateTime.UtcNow;
            Console.WriteLine("[{0} {1}] World: {2}", now.ToShortDateString(), now.ToLongTimeString(), message);
        }
    }
}
