using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jose;
using log4net;
using MiNET.Net;
using MiNET.Utils;
using Newtonsoft.Json.Linq;

namespace MiNET
{
	public class LoginMessageHandler : IMcpeMessageHandler
	{
		private static readonly ILog Log = LogManager.GetLogger(typeof (LoginMessageHandler));

		private readonly PlayerNetworkSession _session;

		private object _loginSyncLock = new object();
		private PlayerInfo _playerInfo;

		public LoginMessageHandler(PlayerNetworkSession session)
		{
			_session = session;
		}

		public void Disconnect(string reason, bool sendDisconnect = true)
		{
		}

		public virtual void HandleMcpeLogin(McpeLogin message)
		{
			//Disconnect("Este servidor ya no existe. Por favor, conecta a " + ChatColors.Aqua + "play.bladestorm.net" + ChatColors.White + " para seguir jugando.");
			////Disconnect("This server is closed. Please connect to " + ChatColors.Aqua + "play.bladestorm.net" + ChatColors.White + " to continue playing.");
			//return;

			// Only one login!
			lock (_loginSyncLock)
			{
				if (_session.Username != null)
				{
					Log.Info($"Player {_session.Username} doing multiple logins");
					return; // Already doing login
				}

				_session.Username = string.Empty;
			}

			if (message.protocolVersion < 81)
			{
				_session.Disconnect($"Wrong version ({message.protocolVersion}) of Minecraft Pocket Edition, please upgrade.");
				return;
			}

			// THIS counter exist to protect the level from being swamped with player list add
			// attempts during startup (normally).

			DecodeCert(message);

			//if (!message.username.Equals("gurun") && !message.username.Equals("TruDan") && !message.username.Equals("Morehs"))
			//{
			//	if (serverInfo.NumberOfPlayers > serverInfo.MaxNumberOfPlayers)
			//	{
			//		Disconnect("Too many players (" + serverInfo.NumberOfPlayers + ") at this time, please try again.");
			//		return;
			//	}

			//	// Use for loadbalance only right now.
			//	if (serverInfo.ConnectionsInConnectPhase > serverInfo.MaxNumberOfConcurrentConnects)
			//	{
			//		Disconnect("Too many concurrent logins (" + serverInfo.ConnectionsInConnectPhase + "), please try again.");
			//		return;
			//	}
			//}

			//if (message.username == null || message.username.Trim().Length == 0 || !Regex.IsMatch(message.username, "^[A-Za-z0-9_-]{3,56}$"))
			//{
			//	Disconnect("Invalid username.");
			//	return;
			//}

			////string fileName = Path.GetTempPath() + "Skin_" + Skin.SkinType + ".png";
			////Log.Info($"Writing skin to filename: {fileName}");
			////Skin.SaveTextureToFile(fileName, Skin.Texture);
		}

		protected void DecodeCert(McpeLogin message)
		{
			_playerInfo = new PlayerInfo();

			// Get bytes
			byte[] buffer = message.payload;

			if (message.payloadLenght != buffer.Length) throw new Exception($"Wrong lenght {message.payloadLenght} != {message.payload.Length}");
			// Decompress bytes

			Log.Debug("Lenght: " + message.payloadLenght + ", Message: " + Convert.ToBase64String(buffer));

			MemoryStream stream = new MemoryStream(buffer);
			if (stream.ReadByte() != 0x78)
			{
				throw new InvalidDataException("Incorrect ZLib header. Expected 0x78 0x9C");
			}
			stream.ReadByte();

			string certificateChain;
			string skinData;

			using (var defStream2 = new DeflateStream(stream, CompressionMode.Decompress, false))
			{
				// Get actual package out of bytes
				using (MemoryStream destination = MiNetServer.MemoryStreamManager.GetStream())
				{

					defStream2.CopyTo(destination);
					destination.Position = 0;
					NbtBinaryReader reader = new NbtBinaryReader(destination, false);

					try
					{
						var countCertData = reader.ReadInt32();
						Log.Debug("Count cert: " + countCertData);
						certificateChain = Encoding.UTF8.GetString(reader.ReadBytes(countCertData));
						Log.Debug("Decompressed certificateChain " + certificateChain);

						var countSkinData = reader.ReadInt32();
						Log.Debug("Count skin: " + countSkinData);
						skinData = Encoding.UTF8.GetString(reader.ReadBytes(countSkinData));
						Log.Debug("Decompressed skinData" + skinData);
					}
					catch (Exception e)
					{
						Log.Error("Parsing login", e);
						return;
					}
				}
			}


			try
			{
				{
					if (Log.IsDebugEnabled) Log.Debug("Input JSON string: " + certificateChain);

					dynamic json = JObject.Parse(certificateChain);

					if (Log.IsDebugEnabled) Log.Debug($"JSON:\n{json}");

					string validationKey = null;
					foreach (dynamic o in json.chain)
					{
						IDictionary<string, dynamic> headers = JWT.Headers(o.ToString());

						if (Log.IsDebugEnabled)
						{
							Log.Debug("Raw chain element:\n" + o.ToString());
							Log.Debug($"JWT Header: {string.Join(";", headers)}");

							dynamic jsonPayload = JObject.Parse(JWT.Payload(o.ToString()));
							Log.Debug($"JWT Payload:\n{jsonPayload}");
						}

						// x5u cert (string): MHYwEAYHKoZIzj0CAQYFK4EEACIDYgAE8ELkixyLcwlZryUQcu1TvPOmI2B7vX83ndnWRUaXm74wFfa5f/lwQNTfrLVHa2PmenpGI6JhIMUJaWZrjmMj90NoKNFSNBuKdm8rYiXsfaz3K36x/1U26HpG0ZxK/V1V
						if (headers.ContainsKey("x5u"))
						{
							string certString = headers["x5u"];

							if (Log.IsDebugEnabled)
							{
								Log.Debug($"x5u cert (string): {certString}");
								ECDiffieHellmanPublicKey publicKey = CryptoUtils.CreateEcDiffieHellmanPublicKey(certString);
								Log.Debug($"Cert:\n{publicKey.ToXmlString()}");
							}

							// Validate
							CngKey newKey = CryptoUtils.ImportECDsaCngKeyFromString(certString);
							CertificateData data = JWT.Decode<CertificateData>(o.ToString(), newKey);

							if (data != null)
							{
								if (Log.IsDebugEnabled) Log.Debug("Decoded token success");

								if (CertificateData.MojangRootKey.Equals(certString, StringComparison.InvariantCultureIgnoreCase))
								{
									Log.Debug("Got Mojang key. Is valid = " + data.CertificateAuthority);
									validationKey = data.IdentityPublicKey;
								}
								else if (validationKey != null && validationKey.Equals(certString, StringComparison.InvariantCultureIgnoreCase))
								{
									_playerInfo.CertificateData = data;
								}
								else
								{
									if (data.ExtraData == null) continue;

									// Self signed, make sure they don't fake XUID
									if (data.ExtraData.Xuid != null)
									{
										Log.Warn("Received fake XUID from " + data.ExtraData.DisplayName);
										data.ExtraData.Xuid = null;
									}

									_playerInfo.CertificateData = data;
								}
							}
							else
							{
								Log.Error("Not a valid Identity Public Key for decoding");
							}
						}
					}

					//TODO: Implement disconnect here

					{
						_playerInfo.Username = _playerInfo.CertificateData.ExtraData.DisplayName;
						_session.Username = _playerInfo.Username;
						string identity = _playerInfo.CertificateData.ExtraData.Identity;

						if (Log.IsDebugEnabled) Log.Debug($"Connecting user {_playerInfo.Username} with identity={identity}");
						_playerInfo.ClientUuid = new UUID(new Guid(identity));

						_session.CryptoContext = new CryptoContext
						{
							UseEncryption = Config.GetProperty("UseEncryptionForAll", false) || (Config.GetProperty("UseEncryption", true) && !string.IsNullOrWhiteSpace(_playerInfo.CertificateData.ExtraData.Xuid)),
						};

						if (_session.CryptoContext.UseEncryption)
						{
							ECDiffieHellmanPublicKey publicKey = CryptoUtils.CreateEcDiffieHellmanPublicKey(_playerInfo.CertificateData.IdentityPublicKey);
							if (Log.IsDebugEnabled) Log.Debug($"Cert:\n{publicKey.ToXmlString()}");

							// Create shared shared secret
							ECDiffieHellmanCng ecKey = new ECDiffieHellmanCng(384);
							ecKey.HashAlgorithm = CngAlgorithm.Sha256;
							ecKey.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
							ecKey.SecretPrepend = Encoding.UTF8.GetBytes("RANDOM SECRET"); // Server token

							byte[] secret = ecKey.DeriveKeyMaterial(publicKey);

							if (Log.IsDebugEnabled) Log.Debug($"SECRET KEY (b64):\n{Convert.ToBase64String(secret)}");

							{
								RijndaelManaged rijAlg = new RijndaelManaged
								{
									BlockSize = 128,
									Padding = PaddingMode.None,
									Mode = CipherMode.CFB,
									FeedbackSize = 8,
									Key = secret,
									IV = secret.Take(16).ToArray(),
								};

								// Create a decrytor to perform the stream transform.
								ICryptoTransform decryptor = rijAlg.CreateDecryptor(rijAlg.Key, rijAlg.IV);
								MemoryStream inputStream = new MemoryStream();
								CryptoStream cryptoStreamIn = new CryptoStream(inputStream, decryptor, CryptoStreamMode.Read);

								ICryptoTransform encryptor = rijAlg.CreateEncryptor(rijAlg.Key, rijAlg.IV);
								MemoryStream outputStream = new MemoryStream();
								CryptoStream cryptoStreamOut = new CryptoStream(outputStream, encryptor, CryptoStreamMode.Write);

								_session.CryptoContext.Algorithm = rijAlg;
								_session.CryptoContext.Decryptor = decryptor;
								_session.CryptoContext.Encryptor = encryptor;
								_session.CryptoContext.InputStream = inputStream;
								_session.CryptoContext.OutputStream = outputStream;
								_session.CryptoContext.CryptoStreamIn = cryptoStreamIn;
								_session.CryptoContext.CryptoStreamOut = cryptoStreamOut;
							}

							var response = McpeServerExchange.CreateObject();
							response.NoBatch = true;
							response.ForceClear = true;
							response.serverPublicKey = Convert.ToBase64String(ecKey.PublicKey.GetDerEncoded());
							response.tokenLenght = (short) ecKey.SecretPrepend.Length;
							response.token = ecKey.SecretPrepend;

							_session.SendPackage(response);

							if (Log.IsDebugEnabled) Log.Warn($"Encryption enabled for {_session.Username}");
						}
					}
				}

				{
					if (Log.IsDebugEnabled) Log.Debug("Input SKIN string: " + skinData);

					IDictionary<string, dynamic> headers = JWT.Headers(skinData);
					dynamic payload = JObject.Parse(JWT.Payload(skinData));

					if (Log.IsDebugEnabled) Log.Debug($"Skin JWT Header: {string.Join(";", headers)}");
					if (Log.IsDebugEnabled) Log.Debug($"Skin JWT Payload:\n{payload.ToString()}");

					// Skin JWT Payload: 
					//{
					// "ClientRandomId": -1256727416,
					// "ServerAddress": "yodamine.com:19132",
					// "SkinData": "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Af/3/wH/9/8B//f/Af/3/wAAAP/yrmj/8q5o//KuaP/yrmj/8q5o//KuaP/yrmj/8q5o/wMCAv8B//f/AAAA/wAAAP8AAAD/AAAA/wAAAP8AAAD//wAA/wAAAP8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAgD/AQEB/wEBAf8BAQH//fn2//359v/9+fb//fn2/wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQUF/wkICP8JCQn/EhIS//KuaP/yrmj/AAAA/wkJCf8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA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",
					// "SkinId": "Standard_Custom"
					//}

					_playerInfo.ServerAddress = payload.ServerAddress;
					_playerInfo.ClientId = payload.ClientRandomId;

					_playerInfo.Skin = new Skin()
					{
						SkinType = payload.SkinId,
						Texture = Convert.FromBase64String((string) payload.SkinData),
					};
				}

				if (!_session.CryptoContext.UseEncryption)
				{
					_session.MessageHandler.HandleMcpeClientMagic(null);
				}
			}
			catch (Exception e)
			{
				Log.Error("Decrypt", e);
			}
		}

		public void HandleMcpeClientMagic(McpeClientMagic message)
		{
			IServerManager serverManager = _session.Server.ServerManager;
			IServer server = serverManager.GetServer();

			IMcpeMessageHandler messageHandler = server.CreatePlayer(_session, _playerInfo);
			_session.MessageHandler = messageHandler; // Replace current message handler with real one.

			_session.MessageHandler.HandleMcpeClientMagic(null);
		}

		public void HandleMcpeText(McpeText message)
		{
		}

		public void HandleMcpeMovePlayer(McpeMovePlayer message)
		{
		}

		public void HandleMcpeRemoveBlock(McpeRemoveBlock message)
		{
		}

		public void HandleMcpeEntityEvent(McpeEntityEvent message)
		{
		}

		public void HandleMcpeMobEquipment(McpeMobEquipment message)
		{
		}

		public void HandleMcpeMobArmorEquipment(McpeMobArmorEquipment message)
		{
		}

		public void HandleMcpeInteract(McpeInteract message)
		{
		}

		public void HandleMcpeUseItem(McpeUseItem message)
		{
		}

		public void HandleMcpePlayerAction(McpePlayerAction message)
		{
		}

		public void HandleMcpeAnimate(McpeAnimate message)
		{
		}

		public void HandleMcpeRespawn(McpeRespawn message)
		{
		}

		public void HandleMcpeDropItem(McpeDropItem message)
		{
		}

		public void HandleMcpeContainerClose(McpeContainerClose message)
		{
		}

		public void HandleMcpeContainerSetSlot(McpeContainerSetSlot message)
		{
		}

		public void HandleMcpeCraftingEvent(McpeCraftingEvent message)
		{
		}

		public void HandleMcpeBlockEntityData(McpeBlockEntityData message)
		{
		}

		public void HandleMcpePlayerInput(McpePlayerInput message)
		{
		}

		public void HandleMcpeMapInfoRequest(McpeMapInfoRequest message)
		{
		}

		public void HandleMcpeRequestChunkRadius(McpeRequestChunkRadius message)
		{
		}

		public void HandleMcpeItemFramDropItem(McpeItemFramDropItem message)
		{
		}
	}

	public interface IServerManager
	{
		IServer GetServer();
	}

	public interface IServer
	{
		IMcpeMessageHandler CreatePlayer(INetworkHandler session, PlayerInfo playerInfo);
	}

	public class DefualtServerManager: IServerManager
	{
		private readonly MiNetServer _miNetServer;
		private IServer _getServer;

		protected DefualtServerManager()
		{
			
		}

		public DefualtServerManager(MiNetServer miNetServer)
		{
			_miNetServer = miNetServer;
			_getServer = new DefaultServer(miNetServer);
		}

		public virtual IServer GetServer()
		{
			return _getServer;
		}
	}

	public class DefaultServer: IServer
	{
		private readonly MiNetServer _server;

		protected DefaultServer()
		{
		}

		public DefaultServer(MiNetServer server)
		{
			_server = server;
		}

		public virtual IMcpeMessageHandler CreatePlayer(INetworkHandler session, PlayerInfo playerInfo)
		{
			Player player = _server.PlayerFactory.CreatePlayer(_server, session.GetClientEndPoint());
			player.NetworkHandler = session;
			player.CertificateData = playerInfo.CertificateData;
			player.Username = playerInfo.Username;
			player.ClientUuid = playerInfo.ClientUuid;
			player.ServerAddress = playerInfo.ServerAddress;
			player.ClientId = playerInfo.ClientId;
			player.Skin = playerInfo.Skin;

			return player;
		}
	}
}