﻿// Part of FemtoCraft | Copyright 2012-2013 Matvei Stefarov <me@matvei.org> | See LICENSE.txt
// Based on fCraft.Player - fCraft is Copyright 2009-2012 Matvei Stefarov <me@matvei.org> | See LICENSE.fCraft.txt
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using JetBrains.Annotations;

namespace Testosterone {
    public sealed partial class Player {
        readonly Server server;

        public Server Server {
            get { return server; }
        }


        [NotNull]
        public string Name { get; private set; }
        public byte Id { get; set; }

        [NotNull]
        public IPAddress IP { get; private set; }
        public Position Position { get; set; }

        [NotNull]
        public Map Map { get; set; }
        Map mapToJoin;

        bool isOp;
        public bool IsOp {
            get {
                return isOp;
            }
            set {
                if( value == isOp )
                    return;
                isOp = value;
                if( SupportsBlockPermissions ) {
                    SendBlockPermissions();
                } else {
                    Send( Packet.MakeSetPermission( CanUseSolid ) );
                }
            }
        }

        public bool HasRegistered { get; set; }
        public bool HasBeenAnnounced { get; private set; }
        public bool IsPainting { get; set; }
        public DateTime LastActiveTime { get; private set; }

        const int Timeout = 10000,
                  SleepDelay = 5;
        readonly TcpClient client;
        NetworkStream stream;
        LoggingStream loggingStream;
        public PacketReader Reader;
        public PacketWriter Writer;

        static readonly TimeSpan ThrottleInterval = new TimeSpan( 0, 0, 1 );
        DateTime throttleCheckTimer;
        int throttlePacketCount;
        const int ThrottleThreshold = 2500;

        volatile bool canReceive = true,
                      canSend = true,
                      canQueue = true;
        

        readonly MovementHandler movementHandler;


        internal Player( [NotNull]Server server, [NotNull] string name ) {
            if (server == null) throw new ArgumentNullException("server");
            this.server = server;
            Name = name;
            ClientName = "Unknown";
            IsOp = true;
            movementHandler = new MovementHandler(this);
        }


        public Player( [NotNull]Server server, [NotNull] TcpClient newClient ) {
            if (server == null) throw new ArgumentNullException("server");
            if( newClient == null ) throw new ArgumentNullException( "newClient" );
            this.server = server;
            try {
                client = newClient;
                movementHandler = new MovementHandler(this);
                Thread thread = new Thread( IoThread ) {
                    IsBackground = true
                };
                thread.Start();

            } catch( Exception ex ) {
                Logger.LogError( "Could not start a player session: {0}", ex );
                Disconnect();
            }
        }


        void IoThread() {
            try {
                client.SendTimeout = Timeout;
                client.ReceiveTimeout = Timeout;
                client.NoDelay = true;
                IP = ((IPEndPoint)(client.Client.RemoteEndPoint)).Address;
                Name = "from " + IP; // placeholder for logging
                stream = client.GetStream();
                loggingStream = new LoggingStream(stream);
                Reader = new PacketReader( server.PacketManager, loggingStream );
                Writer = new PacketWriter( server.PacketManager, loggingStream );
                throttleCheckTimer = DateTime.UtcNow + ThrottleInterval;

                if( !LoginSequence() ) return;

                while( canSend ) {
                    // Write normal packets to output
                    while( sendQueue.Count > 0 ) {
                        Packet packet;
                        lock( sendQueueLock ) {
                            packet = sendQueue.Dequeue();
                        }
                        if( packet.OpCode == OpCode.SetBlockServer ) {
                            ProcessOutgoingSetBlock( ref packet );
                        }
                        Writer.Write( packet.Bytes );
                        if( packet.OpCode == OpCode.Kick ) {
                            Writer.Flush();
                            return;
                        }
                    }

                    // Write SetBlock packets to output
                    while( blockSendQueue.Count > 0 && throttlePacketCount < ThrottleThreshold && canSend ) {
                        Packet packet;
                        lock( blockSendQueueLock ) {
                            packet = blockSendQueue.Dequeue();
                        }
                        Writer.Write( packet.Bytes );
                        throttlePacketCount++;
                    }
                    if( DateTime.UtcNow > throttleCheckTimer ) {
                        throttlePacketCount = 0;
                        throttleCheckTimer += ThrottleInterval;
                    }

                    // Check if a map change is pending. Resend map if it is.
                    if( mapToJoin != Map ) {
                        Map = mapToJoin;
                        for( int i = 1; i < sbyte.MaxValue; i++ ) {
                            Writer.Write( Packet.MakeRemoveEntity( i ).Bytes );
                        }
                        SendMap();
                        Server.SpawnPlayers( this );
                    }

                    // Read input from player
                    while( canReceive && stream.DataAvailable ) {
                        OpCode opCode = Reader.ReadOpCode();
                        switch( opCode ) {
                            case OpCode.Message:
                                if( !ProcessMessagePacket() ) return;
                                break;

                            case OpCode.Teleport:
                                movementHandler.ProcessMovementPacket();
                                break;

                            case OpCode.SetBlockClient:
                                if( !ProcessSetBlockPacket() ) return;
                                break;

                            case OpCode.Ping:
                                break;

                            default:
                                Logger.Log( "Player {0} was kicked after sending an invalid opCode ({1}).",
                                            Name, opCode );
                                KickNow( "Unknown packet opCode " + opCode );
                                return;
                        }
                    }

                    Thread.Sleep( SleepDelay );
                }


            } catch( IOException ) {} catch( SocketException ) {
#if !DEBUG
            } catch( Exception ex ) {
                Logger.LogError( "Player: Session crashed: {0}", ex );
#endif
            } finally {
                canQueue = false;
                canSend = false;
                Disconnect();
            }
        }


        void Disconnect() {
            if( useSyncKick ) {
                kickWaiter.Set();
            } else {
                Server.UnregisterPlayer( this );
            }
            if( stream != null ) stream.Close();
            if( client != null ) client.Close();
        }


        bool LoginSequence() {
            // start reading the first packet
            OpCode opCode = Reader.ReadOpCode();
            if( opCode != OpCode.Handshake ) {
                Logger.LogWarning( "Player from {0}: Unexpected handshake packet opCode ({1})",
                                   IP, opCode );
                return false;
            }

            // check protocol version
            int protocolVersion = Reader.ReadByte();
            if( protocolVersion != Packet.ProtocolVersion ) {
                Logger.LogWarning( "Player from {0}: Wrong protocol version ({1})",
                                   IP, protocolVersion );
                return false;
            }

            // check if name is valid
            string name = Reader.ReadString();
            if( !server.IsValidName( name ) ) {
                KickNow( "Unacceptable player name." );
                Logger.LogWarning( "Player from {0}: Unacceptable player name ({1})",
                                   IP, name );
                return false;
            }

            // check if name is verified
            string mppass = Reader.ReadString();
            byte magicNum = Reader.ReadByte();
            if( server.config.VerifyNames ) {
                while( mppass.Length < 32 ) {
                    mppass = "0" + mppass;
                }
                MD5 hasher = MD5.Create();
                StringBuilder sb = new StringBuilder( 32 );
                foreach( byte b in hasher.ComputeHash( Encoding.ASCII.GetBytes( server.Heartbeat.Salt + name ) ) ) {
                    sb.AppendFormat( "{0:x2}", b );
                }
                bool verified = sb.ToString().Equals( mppass, StringComparison.OrdinalIgnoreCase );
                if( !verified ) {
                    KickNow( "Could not verify player name." );
                    Logger.LogWarning( "Player {0} from {1}: Could not verify name.",
                                       name, IP );
                    return false;
                }
            }
            Name = name;

            // check whitelist
            if( server.config.UseWhitelist && !server.Whitelist.Contains( Name ) ) {
                KickNow( "You are not on the whitelist!" );
                Logger.LogWarning( "Player {0} tried to log in from ({1}), but was not on the whitelist.",
                                   Name, IP );
                return false;
            }

            // negotiate protocol extensions, if applicable TODO: CPE
            /*if( Config.ProtocolExtension && magicNum == 0x42 ) {
                if( !NegotiateProtocolExtension() ) return false;
            }*/

            // check if player is op
            IsOp = server.Ops.Contains( Name );

            // register player and send map
            if( !server.RegisterPlayer( this ) ) return false;

            // write handshake, send map
            Writer.Write( Packet.MakeHandshake( CanUseSolid ).Bytes );
            SendMap();

            // announce player, and print MOTD
            Server.Players.Message( this, false,
                                    "Player {0} connected.", Name );
            HasBeenAnnounced = true;
            //Logger.Log( "Send: Message({0})", Config.MOTD );
            Message( server.config.MOTD );
            server.Commands.PlayersHandler( this );
            return true;
        }


        void SendMap() {
            // write MapBegin
            //Logger.Log( "Send: MapBegin()" );
            Writer.Write( OpCode.MapBegin );

            // grab a compressed copy of the map
            byte[] blockData;
            Map map = server.Map;
            using( MemoryStream mapStream = new MemoryStream() ) {
                using( GZipStream compressor = new GZipStream( mapStream, CompressionMode.Compress ) ) {
                    int convertedBlockCount = IPAddress.HostToNetworkOrder( map.Volume );
                    compressor.Write( BitConverter.GetBytes( convertedBlockCount ), 0, 4 );
                    byte[] rawData = ( UsesCustomBlocks ? map.Blocks : map.GetFallbackMap() );
                    compressor.Write( rawData, 0, rawData.Length );
                }
                blockData = mapStream.ToArray();
            }

            // Transfer the map copy
            byte[] buffer = new byte[1024];
            int mapBytesSent = 0;
            while( mapBytesSent < blockData.Length ) {
                int chunkSize = blockData.Length - mapBytesSent;
                if( chunkSize > 1024 ) {
                    chunkSize = 1024;
                } else {
                    // CRC fix for ManicDigger
                    for( int i = 0; i < buffer.Length; i++ ) {
                        buffer[i] = 0;
                    }
                }
                Buffer.BlockCopy( blockData, mapBytesSent, buffer, 0, chunkSize );
                byte progress = (byte)( 100 * mapBytesSent / blockData.Length );

                // write in chunks of 1024 bytes or less
                //Logger.Log( "Send: MapChunk({0},{1})", chunkSize, progress );
                Writer.Write( OpCode.MapChunk );
                Writer.Write( (short)chunkSize );
                Writer.Write( buffer, 0, 1024 );
                Writer.Write( progress );
                mapBytesSent += chunkSize;
            }

            // write MapEnd
            Writer.Write( OpCode.MapEnd );
            Writer.Write( (short)map.Width );
            Writer.Write( (short)map.Height );
            Writer.Write( (short)map.Length );

            // write spawn point
            Writer.Write( Packet.MakeAddEntity( 255, Name, map.Spawn ).Bytes );
            Writer.Write( Packet.MakeTeleport( 255, map.Spawn ).Bytes );

            movementHandler.LastValidPosition = map.Spawn;
        }


        public void ChangeMap( [NotNull] Map newMap ) {
            if( newMap == null ) throw new ArgumentNullException( "newMap" );
            mapToJoin = newMap;
        }

        #region Send / Kick

        readonly object sendQueueLock = new object(),
                        blockSendQueueLock = new object();
        readonly Queue<Packet> sendQueue = new Queue<Packet>();
        readonly Queue<Packet> blockSendQueue = new Queue<Packet>();

        bool useSyncKick;
        readonly AutoResetEvent kickWaiter = new AutoResetEvent( false );


        public void Send( Packet packet ) {
            if( packet.OpCode == OpCode.SetBlockServer ) {
                lock( blockSendQueueLock ) {
                    if( canQueue ) {
                        blockSendQueue.Enqueue( packet );
                    }
                }
            } else {
                lock( sendQueueLock ) {
                    if( canQueue ) {
                        sendQueue.Enqueue( packet );
                    }
                }
            }
        }


        public void Kick( [NotNull] string message ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            Packet packet = Packet.MakeKick( message );
            lock( sendQueueLock ) {
                canReceive = false;
                canQueue = false;
                sendQueue.Enqueue( packet );
            }
        }


        void KickNow( [NotNull] string message ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            canReceive = false;
            canQueue = false;
            canSend = false;
            Writer.Write( OpCode.Kick );
            Writer.Write( message );
        }


        public void KickSynchronously( [NotNull] string message ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            useSyncKick = true;
            Kick( message );
            kickWaiter.WaitOne();
            Server.UnregisterPlayer( this );
        }

        #endregion


        #region Block Placement

        public bool PlaceWater,
                    PlaceLava,
                    PlaceSolid,
                    PlaceGrass;

        readonly Queue<DateTime> spamBlockLog = new Queue<DateTime>();

        const int AntiGriefBlocks = 47,
                  AntiGriefSeconds = 6,
                  MaxBlockPlacementRange = 7 * 32;


        bool ProcessSetBlockPacket() {
            ResetIdleTimer();
            short x = Reader.ReadInt16();
            short z = Reader.ReadInt16();
            short y = Reader.ReadInt16();
            bool isDeleting = ( Reader.ReadByte() == 0 );
            byte rawType = Reader.ReadByte();

            // check if block type is valid
            if( !UsesCustomBlocks && rawType > (byte)Map.MaxLegalBlockType ||
                UsesCustomBlocks && rawType > (byte)Map.MaxCustomBlockType ) {
                KickNow( "Hacking detected." );
                Logger.LogWarning( "Player {0} tried to place an invalid block type.", Name );
                return false;
            }
            if( IsPainting ) isDeleting = false;
            Block block = (Block)rawType;
            if( isDeleting ) block = Block.Air;

            // check if coordinates are within map boundaries (don't kick)
            if( !Map.InBounds( x, y, z ) ) return true;

            // check if player is close enough to place
            if( !IsOp && server.config.LimitClickDistance || IsOp && server.config.OpLimitClickDistance ) {
                if( Math.Abs( x * 32 - Position.X ) > MaxBlockPlacementRange ||
                    Math.Abs( y * 32 - Position.Y ) > MaxBlockPlacementRange ||
                    Math.Abs( z * 32 - Position.Z ) > MaxBlockPlacementRange ) {
                    KickNow( "Hacking detected." );
                    Logger.LogWarning( "Player {0} tried to place a block too far away.", Name );
                    return false;
                }
            }

            // check click rate
            if( !IsOp && server.config.LimitClickRate || IsOp && server.config.OpLimitClickRate ) {
                if( DetectBlockSpam() ) {
                    KickNow( "Hacking detected." );
                    Logger.LogWarning( "Player {0} tried to place blocks too quickly.", Name );
                    return false;
                }
            }

            // apply blocktype mapping
            if( block == Block.Blue && PlaceWater ) {
                block = Block.Water;
            } else if( block == Block.Red && PlaceLava ) {
                block = Block.Lava;
            } else if( block == Block.Stone && PlaceSolid ) {
                block = Block.Admincrete;
            } else if( block == Block.Dirt && PlaceGrass ) {
                block = Block.Grass;
            }

            // check if blocktype is permitted
            if( ( block == Block.Water || block == Block.StillWater ) && !CanUseWater ||
                ( block == Block.Lava || block == Block.StillLava ) && !CanUseLava ||
                ( block == Block.Grass ) && !CanUseGrass ||
                ( block == Block.Admincrete || block == Block.Admincrete ) && !CanUseSolid ) {
                KickNow( "Hacking detected." );
                Logger.LogWarning( "Player {0} tried to place a restricted block type.", Name );
                return false;
            }

            // check if deleting admincrete
            Block oldBlock = Map.GetBlock( x, y, z );
            if( ( oldBlock == Block.Admincrete ) && !CanUseSolid ) {
                KickNow( "Hacking detected." );
                Logger.LogWarning( "Player {0} tried to delete a restricted block type.", Name );
                return false;
            }

            // update map
            Map.SetBlock( this, x, y, z, block );

            // check if sending back an update is necessary
            Block placedBlock = Map.GetBlock( x, y, z );
            if( IsPainting || ( !isDeleting && placedBlock != (Block)rawType ) ) {
                Writer.Write( Packet.MakeSetBlock( x, y, z, placedBlock ).Bytes );
            }
            return true;
        }


        bool DetectBlockSpam() {
            if( spamBlockLog.Count >= AntiGriefBlocks ) {
                DateTime oldestTime = spamBlockLog.Dequeue();
                double spamTimer = DateTime.UtcNow.Subtract( oldestTime ).TotalSeconds;
                if( spamTimer < AntiGriefSeconds ) {
                    return true;
                }
            }
            spamBlockLog.Enqueue( DateTime.UtcNow );
            return false;
        }

        #endregion


        #region Messaging

        [CanBeNull] string partialMessage;

        const int AntispamMessageCount = 3,
                  AntispamInterval = 4;

        readonly Queue<DateTime> spamChatLog = new Queue<DateTime>( AntispamMessageCount );


        bool ProcessMessagePacket() {
            ResetIdleTimer();
            Reader.ReadByte();
            string message = Reader.ReadString();

            // special handler for WoM id packets
            // (which are erroneously padded with zeroes instead of spaces).
            if( message.StartsWith( "/womid " ) ) return true;

            if( ContainsInvalidChars( message ) ) {
                KickNow( "Hacking detected." );
                Logger.LogWarning( "Player {0} attempted to write illegal characters in chat.",
                                   Name );
                return false;
            }

            if( !IsOp && server.config.LimitChatRate || IsOp && server.config.OpLimitChatRate ) {
                if( DetectChatSpam() ) return false;
            }

            ProcessMessage( message );
            return true;
        }


        public void ProcessMessage( [NotNull] string rawMessage ) {
            if( rawMessage == null ) throw new ArgumentNullException( "rawMessage" );
            if( rawMessage.Length == 0 ) return;

            // cancel partial message
            if( rawMessage.StartsWith( "/nvm", StringComparison.OrdinalIgnoreCase ) ||
                rawMessage.StartsWith( "/cancel", StringComparison.OrdinalIgnoreCase ) ) {
                if( partialMessage != null ) {
                    Message( "Partial message cancelled." );
                    partialMessage = null;
                } else {
                    Message( "No partial message to cancel." );
                }
                return;
            }

            // handle partial messages
            if( partialMessage != null ) {
                rawMessage = partialMessage + rawMessage;
                partialMessage = null;
            }
            if( rawMessage.EndsWith( " /" ) ) {
                partialMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                Message( "Partial: &F{0}", partialMessage );
                return;
            }
            if( rawMessage.EndsWith( " //" ) ) {
                rawMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
            }

            // handle commands
            if( rawMessage[0] == '/' ) {
                if( rawMessage.Length < 2 ) {
                    Message( "Cannot parse message." );
                    return;
                } else if( rawMessage[1] == '/' ) {
                    rawMessage = rawMessage.Substring( 1 );
                } else {
                    server.Commands.Parse( this, rawMessage );
                    return;
                }
            }

            // broadcast chat
            Logger.LogChat( "{0}: {1}", Name, rawMessage );
            if( server.config.RevealOps && IsOp ) {
                Server.Players.Message( null, false,
                                        "{0}{1}&F: {2}",
                                        server.config.OpColor, Name, rawMessage );
            } else {
                Server.Players.Message( null, false,
                                        "&F{0}: {1}",
                                        Name, rawMessage );
            }
        }


        [StringFormatMethod( "message" )]
        public void Message( [NotNull] string message, [NotNull] params object[] formatArgs ) {
            if( formatArgs.Length > 0 ) {
                message = String.Format( message, formatArgs );
            }
            if( this == server.ConsolePlayer ) {
                Console.WriteLine( message );
            } else {
                foreach( Packet p in new LineWrapper( "&E" + message ) ) {
                    Send( p );
                }
            }
        }


        [StringFormatMethod( "message" )]
        public void MessageNow( [NotNull] string message, [NotNull] params object[] formatArgs ) {
            if( formatArgs.Length > 0 ) {
                message = String.Format( message, formatArgs );
            }
            if( this == server.ConsolePlayer ) {
                Console.WriteLine( message );
            } else {
                foreach( Packet p in new LineWrapper( "&E" + message ) ) {
                    Writer.Write( p.Bytes );
                }
            }
        }


        public bool CheckIfOp() {
            if( !IsOp ) Message( "You must be op to do this." );
            return IsOp;
        }


        public bool CheckIfConsole() {
            bool isConsole = (this == server.ConsolePlayer);
            if( isConsole ) Message( "You cannot use this command from console." );
            return isConsole;
        }


        [ContractAnnotation( "givenName:null => false" )]
        public bool CheckPlayerName( [CanBeNull] string givenName ) {
            if( givenName == null ) {
                Message( "This command requires a player name." );
                return false;
            } else if( !server.IsValidName( givenName ) ) {
                Message( "\"{0}\" is not a valid player name.", givenName );
                return false;
            } else {
                return true;
            }
        }


        public bool CheckIfAllowed( bool guestConfigKey, bool opConfigKey ) {
            if( CheckIfConsole() ) return false;
            if( !guestConfigKey ) {
                if( !opConfigKey ) {
                    Message( "This command is disabled on this server." );
                } else if( !CheckIfOp() ) {
                    return false;
                }
            }
            return true;
        }


        bool DetectChatSpam() {
            if( this == server.ConsolePlayer ) return false;
            if( spamChatLog.Count >= AntispamMessageCount ) {
                DateTime oldestTime = spamChatLog.Dequeue();
                if( DateTime.UtcNow.Subtract( oldestTime ).TotalSeconds < AntispamInterval ) {
                    KickNow( "Kicked for chat spam!" );
                    return true;
                }
            }
            spamChatLog.Enqueue( DateTime.UtcNow );
            return false;
        }

        #endregion


        #region Permissions

        bool CanUseWater {
            get { return ( server.config.AllowWaterBlocks || server.config.OpAllowWaterBlocks && IsOp ); }
        }

        bool CanUseLava {
            get { return ( server.config.AllowLavaBlocks || server.config.OpAllowLavaBlocks && IsOp ); }
        }

        bool CanUseGrass {
            get { return ( server.config.AllowGrassBlocks || server.config.OpAllowGrassBlocks && IsOp ); }
        }

        bool CanUseSolid {
            get { return ( server.config.AllowSolidBlocks || server.config.OpAllowSolidBlocks && IsOp ); }
        }

        #endregion


        public void ResetIdleTimer() {
            LastActiveTime = DateTime.UtcNow;
        }



        // checks if message contains any characters that cannot be typed in from Minecraft client
        static bool ContainsInvalidChars( [NotNull] string message ) {
            return message.Any( t => t < ' ' || t == '&' || t > '~' );
        }
    }
}