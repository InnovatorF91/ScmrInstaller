using Microsoft.Win32;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ScmrInstaller
{
	internal class Program
	{
		// ====== 可按需调整的常量 ======
		// 在 SC2 里我们只会使用 Maps 与 Mods 两个目录（按视频做“复制粘贴”）
		private const string MapsFolderName = "Maps";
		private const string ModsFolderName = "Mods";

		// 在 Mods 内，以下子包会被放入 Mods；Cinematics 若存在旧版将被覆盖（视频说要替换）
		private static readonly string[] ModsLikeFolderNames = { "Mods", "Assets", "Localization", "Cinematics" };

		// 安装器工作目录
		private static string WorkDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		// 下载相关参数（可按需调整）
		private static readonly TimeSpan HttpTimeoutPerRequest = TimeSpan.FromMinutes(20); // 单个文件最大允许下载 20 分钟
		private static readonly TimeSpan OverallDownloadTimeout = TimeSpan.FromMinutes(60); // 所有下载最多等 60 分钟
		private const int MaxDownloadRetries = 2; // 失败后重试两次
		private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5); // 重试前等待 5 秒

		private static async System.Threading.Tasks.Task<int> Main(string[] args)
		{
			Title("StarCraft: Mass Recall 自动下载安装器");

			// 0. 管理员检查（若非管理员则请求以管理员身份重新启动）
			if (!IsAdmin())
			{
				Warn("正在尝试以管理员身份重新启动安装器……");

				try
				{
					var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
					var psi = new System.Diagnostics.ProcessStartInfo
					{
						FileName = exePath,
						UseShellExecute = true,
						Verb = "runas"   // 关键：要求以管理员方式运行
					};
					System.Diagnostics.Process.Start(psi);
					Environment.Exit(0); // 结束当前非管理员进程
				}
				catch (Exception ex)
				{
					Error($"无法自动提权：{ex.Message}");
					Warn("请右键该程序并选择『以管理员身份运行』。");
					PauseExit(1);
				}
			}

			// 1) 检测 SC2 安装
			var sc2Path = DetectSc2InstallPath();
			if (string.IsNullOrEmpty(sc2Path) || !Directory.Exists(sc2Path))
			{
				Error("未检测到《星际争霸 II》客户端。请先安装 SC2，再运行本安装器。");
				PauseExit(2);
				return 2;
			}
			Info($"已检测到 SC2 安装目录：{sc2Path}");

			// 准备目标目录
			var targetMaps = Path.Combine(sc2Path, MapsFolderName);
			var targetMods = Path.Combine(sc2Path, ModsFolderName);
			Directory.CreateDirectory(targetMaps);
			Directory.CreateDirectory(targetMods);

			// 2) 在线/离线与安装包获取
			bool online = IsOnline();
			Info($"当前网络状态：{(online ? "在线" : "离线")}");

			// 尝试加载 packages.json 清单
			var manifest = LoadManifest(Path.Combine(WorkDir, "packages.json"));

			Info("是否下载安装包（安装包下载时可能会花费您一点时间。）");
			Info("是……a / 否……b");
			var key = Console.ReadKey();
			Console.WriteLine();
			while (key.Key != ConsoleKey.A && key.Key != ConsoleKey.B)
			{
				Info("请输入有效选项：a（是）或 b（否）");
				key = Console.ReadKey();
				Console.WriteLine();
			}

			if (online && manifest != null && manifest.Packages.Any()&&key.Key == ConsoleKey.A)
			{
				Info("检测到 packages.json，开始自动下载安装包……");

				var downloadDir = Path.Combine(WorkDir, "_downloads");

				Directory.CreateDirectory(WorkDir);
				var downloadedZips = await DownloadAllAsync(manifest.Packages, downloadDir);
				if (!downloadedZips.Any())
				{
					Warn("自动下载失败或未获取到 ZIP。将改为尝试本地包或手动方式。");
				}
				else
				{
					WorkDir = downloadDir;
				}
			}
			else if (online && (manifest == null || !manifest.Packages.Any()) && key.Key == ConsoleKey.A)
			{
				Warn("在线，但未提供 packages.json 直链清单。");
				Warn("将为你打开 CurseForge 页面，请下载需要的 ZIP 并放到安装器同目录后按任意键继续。");
				try 
				{ 
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo 
					{ 
						FileName = "https://www.curseforge.com/sc2/maps/starcraft-mass-recall", UseShellExecute = true 
					}); 
				} catch { }
				Console.WriteLine("下载完成后请将 ZIP 放在本安装器同目录，然后按任意键继续……");
				Console.ReadKey(true);
			}
			else
			{
				Info("离线模式：将从本地同目录寻找安装包 ZIP。");
			}

			// 3) 搜集 ZIP
			var zipFiles = FindLocalZipPackages(WorkDir);
			if (!zipFiles.Any())
			{
				Error("未找到任何安装包 ZIP。请准备安装包（或提供 packages.json 以自动下载），然后重试。");
				PauseExit(3);
				return 3;
			}
			Success($"找到 {zipFiles.Count} 个 ZIP：\n - " + string.Join("\n - ", zipFiles.Select(Path.GetFileName)));

			// 4) 解压到临时目录
			var tempExtractRoot = Path.Combine(WorkDir, "_extract_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
			Directory.CreateDirectory(tempExtractRoot);
			foreach (var zip in zipFiles)
			{
				try
				{
					var name = Path.GetFileNameWithoutExtension(zip);
					var dest = Path.Combine(tempExtractRoot, name);
					Directory.CreateDirectory(dest);
					ZipFile.ExtractToDirectory(zip, dest, overwriteFiles: true);
					Info($"解压完成：{Path.GetFileName(zip)}");
				}
				catch (Exception ex)
				{
					Error($"解压失败：{Path.GetFileName(zip)} => {ex.Message}");
					PauseExit(4);
					return 4;
				}
			}

			// 5) 执行“复制粘贴式”安装
			// 规则（与视频一致）：
			// - 把包含“StarCraft Mass Recall”/ “Mass Recall” 的地图主目录复制到 SC2\\Maps
			// - 把 Mods / Assets / Localization / Cinematics 等复制到 SC2\\Mods（Cinematics 若存在则覆盖）
			// - Redux/Extras 若包含地图，放入 SC2\\Maps\\StarCraft Mass Recall\\(Extras/Redux)
			Info("开始复制文件（仅复制，不移动/删除原文件）……");
			int copiedCount = 0;

			// 5.1 尝试识别“主地图（Mass Recall）目录”
			var extractedDirs = Directory.GetDirectories(tempExtractRoot, "*", SearchOption.TopDirectoryOnly);
			foreach (var dir in extractedDirs)
			{
				// 递归扫描每个解压出来的顶层目录，根据其内部结构执行放置
				copiedCount += CopyRecognizedContent(dir, targetMaps, targetMods);
			}

			if (copiedCount == 0)
			{
				Warn("未在解压内容中识别到可复制项目。请核对安装包是否正确。");
				PauseExit(5);
				return 5;
			}
			Success($"复制完成，共复制 {copiedCount} 个文件/目录项。");

			// 回收误放在 Maps 根目录的 .SC2Map 到 'Starcraft Mass Recall'
			var sc2MapsRoot = Path.Combine(sc2Path, "Maps");
			var massRecallRoot = Path.Combine(sc2MapsRoot, "Starcraft Mass Recall");
			var moved = CleanupOrphanSc2Maps(sc2MapsRoot, massRecallRoot);
			if (moved > 0) Warn($"已将 {moved} 个误放在 Maps 根的 SC2Map 文件移回 'Starcraft Mass Recall'。");


			// 6) 创建桌面快捷方式（指向 SC2Switcher_x64，并附带“启动器地图”的路径参数）
			var launcherMap = TryFindLauncherMap(Path.Combine(targetMaps));

			// 确保路径正确
			targetMaps = Path.Combine(targetMaps, "Starcraft Mass Recall");

			if (string.IsNullOrEmpty(launcherMap))
			{
				Warn("未自动找到“启动器地图（Launcher）”。你仍可在 SC2 编辑器中手动打开 Launcher 地图运行。");
			}
			else
			{
				var switcherPath = FindSc2Switcher(sc2Path);
				if (File.Exists(switcherPath))
				{
					var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
					var lnk = Path.Combine(desktop, "StarCraft Mass Recall.lnk");
					CreateShortcut(lnk, switcherPath, $"\"{launcherMap}\"", sc2Path, Path.Combine(targetMaps, "icon.ico")); // 若有图标则使用
					Success($"已创建桌面快捷方式：{lnk}");
				}
				else
				{
					Warn("未找到 SC2Switcher_x64 可执行文件。已跳过创建快捷方式。");
				}
			}

			// 7) 结束
			Success("安装完成！可在游戏内或通过快捷方式启动。");
			PauseExit(0);
			return 0;
		}

		// ========== 安装逻辑辅助 ==========

		/// <summary>
		/// 复制识别到的内容到目标 Maps 与 Mods 目录
		/// </summary>
		/// <param name="extractedTop">解压后的顶层目录</param>
		/// <param name="targetMaps">目标 Maps 目录</param>
		/// <param name="targetMods">目标 Mods 目录</param>
		/// <returns>复制的文件数量</returns>
		private static int CopyRecognizedContent(string extractedTop, string targetMaps, string targetMods)
		{
			int count = 0;

			// 目标中的 Mass Recall 根
			string massRecallTarget = Path.Combine(targetMaps, "Starcraft Mass Recall");

			// 1) 主地图树：解压内容中，不论层级，寻找目录名为 "Starcraft Mass Recall" 或含 "Mass Recall"/"SCMR"
			string? massRecallSource = Directory.GetDirectories(extractedTop, "*", SearchOption.AllDirectories)
				.FirstOrDefault(d =>
					string.Equals(Path.GetFileName(d), "Starcraft Mass Recall", StringComparison.OrdinalIgnoreCase) ||
					Path.GetFileName(d).Contains("Mass Recall", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(Path.GetFileName(d), "SCMR", StringComparison.OrdinalIgnoreCase));

			if (!string.IsNullOrEmpty(massRecallSource))
			{
				Info($"复制主地图：{massRecallSource} -> {massRecallTarget}");
				count += CopyDirectory(massRecallSource, massRecallTarget, overwrite: true);
			}

			// 2) Enslavers Redux：Extras\8. Enslavers Redux
			string? reduxSource = Directory.GetDirectories(extractedTop, "*", SearchOption.AllDirectories)
				.FirstOrDefault(d =>
					d.EndsWith(Path.Combine("Extras", "8. Enslavers Redux"), StringComparison.OrdinalIgnoreCase) ||
					d.EndsWith(Path.Combine("Extras", "Enslavers Redux"), StringComparison.OrdinalIgnoreCase)); // 兼容部分包名

			if (!string.IsNullOrEmpty(reduxSource))
			{
				string reduxTarget = Path.Combine(massRecallTarget, "Extras", Path.GetFileName(reduxSource));
				Info($"复制 Redux：{reduxSource} -> {reduxTarget}");
				count += CopyDirectory(reduxSource, reduxTarget, overwrite: true);
			}

			// 3) 若压缩包顶层直接给了 "Maps" 目录，也一并保持结构复制到 SC2\Maps
			var mapsCandidate = Path.Combine(extractedTop, "Maps");
			if (Directory.Exists(mapsCandidate))
			{
				Info($"复制 Maps：{mapsCandidate} -> {targetMaps}");
				count += CopyDirectory(mapsCandidate, targetMaps, overwrite: true);
			}

			// 4) Mods/Assets/Localization/Cinematics 全部丢到 SC2\Mods（Cinematics/Assets 覆盖）
			foreach (var modLike in ModsLikeFolderNames)
			{
				string? modDir = Directory.GetDirectories(extractedTop, modLike, SearchOption.AllDirectories)
										  .FirstOrDefault(d => string.Equals(Path.GetFileName(d), modLike, StringComparison.OrdinalIgnoreCase));
				if (!string.IsNullOrEmpty(modDir))
				{
					bool overwrite = modLike.Equals("Cinematics", StringComparison.OrdinalIgnoreCase) ||
									 modLike.Equals("Assets", StringComparison.OrdinalIgnoreCase);
					Info($"复制 {modLike}：{modDir} -> {targetMods}");
					count += CopyDirectory(modDir, targetMods, overwrite: overwrite);
				}
			}

			// 5) 兜底：若还有散落的 .SC2Map/.SC2Mod（极少发生），统一落到正确根
			//    - .SC2Map 放到 Mass Recall 根（不要放在 Maps 根）
			//    - .SC2Mod 放到 Mods 根
			var looseMaps = Directory.EnumerateFiles(extractedTop, "*.SC2Map", SearchOption.AllDirectories).ToList();
			foreach (var file in looseMaps)
			{
				var dest = Path.Combine(massRecallTarget, Path.GetFileName(file));
				Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
				File.Copy(file, dest, overwrite: true);
				count++;
			}

			var looseMods = Directory.EnumerateFiles(extractedTop, "*.SC2Mod", SearchOption.AllDirectories).ToList();
			foreach (var file in looseMods)
			{
				var dest = Path.Combine(targetMods, Path.GetFileName(file));
				Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
				File.Copy(file, dest, overwrite: true);
				count++;
			}

			return count;
		}

		/// <summary>
		/// 递归复制目录内容
		/// </summary>
		/// <param name="sourceDir">资源目录</param>
		/// <param name="targetDir">目标目录</param>
		/// <param name="overwrite">是否覆盖已存在文件</param>
		/// <returns>复制的文件数量</returns>
		private static int CopyDirectory(string sourceDir, string targetDir, bool overwrite)
		{
			int count = 0;
			Directory.CreateDirectory(targetDir);

			// 先创建所有目录
			foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
			{
				var rel = Path.GetRelativePath(sourceDir, dir);
				Directory.CreateDirectory(Path.Combine(targetDir, rel));
			}

			// 再复制所有文件
			foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
			{
				var rel = Path.GetRelativePath(sourceDir, file);
				var dest = Path.Combine(targetDir, rel);
				Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
				if (!overwrite && File.Exists(dest)) continue;// 跳过已存在文件
				File.Copy(file, dest, overwrite: overwrite);
				count++;
			}

			return count;
		}

		/// <summary>
		/// 清理孤立的 SC2Map 文件（移动到指定目录）
		/// </summary>
		/// <param name="sc2MapsRoot">SC2 Maps 根目录</param>
		/// <param name="massRecallRoot">Mass Recall 目录</param>
		/// <returns>移动的文件数量</returns>
		private static int CleanupOrphanSc2Maps(string sc2MapsRoot, string massRecallRoot)
		{
			Directory.CreateDirectory(massRecallRoot);
			int moved = 0;
			foreach (var file in Directory.EnumerateFiles(sc2MapsRoot, "*.SC2Map", SearchOption.TopDirectoryOnly))
			{
				var dest = Path.Combine(massRecallRoot, Path.GetFileName(file));
				try
				{
					if (File.Exists(dest)) File.Delete(dest);
					File.Move(file, dest);
					moved++;
				}
				catch { /* 忽略单个失败 */ }
			}
			return moved;
		}


		/// <summary>
		/// 尝试在 Maps 目录下寻找“启动器地图”（Launcher）
		/// </summary>
		/// <param name="mapsRoot">Maps 根目录</param>
		/// <returns>地图路径，未找到返回空字符串</returns>
		private static string TryFindLauncherMap(string mapsRoot)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(mapsRoot) || !Directory.Exists(mapsRoot))
					return string.Empty;

				// 仅允许：{mapsRoot}\Starcraft Mass Recall\SCMR Campaign Launcher.SC2Map
				// 说明：
				// - 目录名固定为 "Starcraft Mass Recall"（大小写不敏感）
				// - 文件名固定为 "SCMR Campaign Launcher.SC2Map"（大小写不敏感）
				// - 不再搜索其他位置或任何“模糊匹配”
				var recallDir = Path.Combine(mapsRoot, "Starcraft Mass Recall");
				if (!Directory.Exists(recallDir))
					return string.Empty;

				var exactPath = Path.Combine(recallDir, "SCMR Campaign Launcher.SC2Map");
				if (File.Exists(exactPath))
					return exactPath;

				// 保险：某些文件系统/压缩包可能改了大小写，这里做一次“大小写无关”的精确名匹配
				// 只在该目录（不递归）中寻找同名文件（忽略大小写）
				var matched = Directory.EnumerateFiles(recallDir, "*.SC2Map", SearchOption.TopDirectoryOnly)
									   .FirstOrDefault(p => string.Equals(
										   Path.GetFileName(p),
										   "SCMR Campaign Launcher.SC2Map",
										   StringComparison.OrdinalIgnoreCase));

				return matched ?? string.Empty;
			}
			catch
			{
				return string.Empty;
			}
		}

		/// <summary>
		/// 寻找 SC2Switcher 可执行文件路径
		/// </summary>
		/// <param name="sc2Path">SC2 安装根目录</param>
		/// <returns>可执行文件路径</returns>
		private static string FindSc2Switcher(string sc2Path)
		{
			// 根据视频提示：SC2\\Support64\\SC2Switcher_x64.exe（或较新版本为 StarCraft II.exe 直启）
			var candidate1 = Path.Combine(sc2Path, "Support64", "SC2Switcher_x64.exe");
			if (File.Exists(candidate1)) return candidate1;

			var candidate2 = Path.Combine(sc2Path, "Support64", "SC2Switcher.exe");
			if (File.Exists(candidate2)) return candidate2;

			// 兜底：直接用 StarCraft II.exe 也能启动地图（但体验不如 Switcher）
			var candidate3 = Path.Combine(sc2Path, "StarCraft II.exe");
			return candidate3;
		}

		// ========== 在线/离线与下载 ==========

		/// <summary>
		/// 加载安装包清单 JSON
		/// </summary>
		/// <param name="path">JSON 文件路径</param>
		/// <returns>安装包清单对象，失败返回 null </returns>
		private static InstallerManifest? LoadManifest(string path)
		{
			try
			{
				if (!File.Exists(path)) return null;
				var json = File.ReadAllText(path);
				var m = JsonSerializer.Deserialize<InstallerManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
				return m;
			}
			catch
			{
				Warn("packages.json 解析失败，忽略此文件。");
				return null;
			}
		}

		/// <summary>
		/// 在指定目录寻找本地 ZIP 包
		/// </summary>
		/// <param name="root">搜索根目录</param>
		/// <returns>找到的 ZIP 文件路径列表</returns>
		private static List<string> FindLocalZipPackages(string root)
		{
			// 在安装器同目录寻找 zip，按名称关键词做粗略分类（无需严格文件名）
			return Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly).ToList();
		}

		/// <summary>
		/// 下载所有 URL 指向的 ZIP 包
		/// </summary>
		/// <param name="urls">URL 列表</param>
		/// <param name="downloadsDir">下载保存目录</param>
		/// <returns>已保存的文件路径列表</returns>
		private static async Task<List<string>> DownloadAllAsync(IEnumerable<string> urls, string downloadsDir)
		{
			var saved = new List<string>();
			Directory.CreateDirectory(downloadsDir);

			using var handler = new HttpClientHandler
			{
				AllowAutoRedirect = true,
				AutomaticDecompression = DecompressionMethods.All
			};
			using var client = new HttpClient(handler)
			{
				Timeout = HttpTimeoutPerRequest
			};

			using var overallCts = new CancellationTokenSource(OverallDownloadTimeout);

			int index = 0;
			var urlList = urls.ToList();
			foreach (var url in urlList)
			{
				index++;
				string fileName = GetFileNameFromUrlOrDefault(url);
				string savePath = Path.Combine(downloadsDir, fileName);

				bool ok = false;

				for (int attempt = 0; attempt <= MaxDownloadRetries; attempt++)
				{
					try
					{
						Info($"开始下载（{index}/{urlList.Count}，尝试 {attempt + 1}/{MaxDownloadRetries + 1}）：{url}");

						// 为每次尝试创建单独的取消令牌
						using var perTryCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
						perTryCts.CancelAfter(HttpTimeoutPerRequest);

						Info("正在下载，请稍后……");

						// 开始下载
						using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, perTryCts.Token);
						resp.EnsureSuccessStatusCode();

						// 尝试从 Content-Disposition 头获取更准确的文件名
						var cdName = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
						if (!string.IsNullOrWhiteSpace(cdName))
						{
							fileName = cdName;
							savePath = Path.Combine(downloadsDir, fileName);
						}

						// 保存到文件
						await using (var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true))
						await using (var rs = await resp.Content.ReadAsStreamAsync(perTryCts.Token))
						{
							// 流式复制
							await CopyStreamAsync(rs, fs, perTryCts.Token);
						}

						// 完成
						Success($"下载成功：{fileName}");
						saved.Add(savePath);
						ok = true;
						break;
					}
					catch (OperationCanceledException)
					{
						Warn($"下载超时：{url}");
					}
					catch (Exception ex)
					{
						Warn($"下载失败：{url}\n原因：{ex.Message}");
					}

					if (!ok && attempt < MaxDownloadRetries)
					{
						await Task.Delay(RetryDelay);
						Info("准备重试……");
					}
				}

				if (!ok)
				{
					// 失败即中止：抛出异常，由调用方切换到“解压本地包”的策略
					throw new InvalidOperationException($"下载未完成：在第 {index}/{urlList.Count} 个包处失败。");
				}
			}

			return saved;
		}

		/// <summary>
		/// 从 URL 推导文件名，失败则返回随机命名
		/// </summary>
		/// <param name="url">URL 地址</param>
		/// <returns>文件名</returns>
		private static string GetFileNameFromUrlOrDefault(string url)
		{
			try
			{
				var last = url.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
				if (!string.IsNullOrWhiteSpace(last))
				{
					// 有些直链会带 query，截掉
					var q = last.IndexOf('?', StringComparison.Ordinal);
					if (q >= 0) last = last.Substring(0, q);
					if (last.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
						return last;
				}
			}
			catch { }
			return "package_" + Guid.NewGuid().ToString("N") + ".zip";
		}

		/// <summary>
		/// 流式复制数据（异步）
		/// </summary>
		/// <param name="input">输入流</param>
		/// <param name="output">输出流</param>
		/// <param name="ct">取消令牌</param>
		/// <returns>任务</returns>
		private static async System.Threading.Tasks.Task CopyStreamAsync(Stream input, Stream output, System.Threading.CancellationToken ct)
		{
			// 使用 64KB 缓冲区进行复制
			var buffer = new byte[1024 * 64];
			int read;

			// 循环读取并写入
			while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
			{
				// 写入读取的字节数
				await output.WriteAsync(buffer.AsMemory(0, read), ct);
			}
		}

		/// <summary>
		/// 从 URL 中提取安全的文件名
		/// </summary>
		/// <param name="url">URL 地址</param>
		/// <returns>文件名，失败返回 null </returns>
		private static string? GetSafeFileNameFromUrl(string url)
		{
			try
			{
				var uri = new Uri(url);
				var name = Path.GetFileName(uri.LocalPath);
				if (string.IsNullOrWhiteSpace(name) || !name.Contains('.')) return null;
				return name;
			}
			catch { return null; }
		}

		/// <summary>
		/// 检查当前进程是否联网
		/// </summary>
		/// <returns>true: 联网 / false: 未联网 </returns>
		private static bool IsOnline()
		{
			try
			{
				// 快速检测网络可用性
				if (!NetworkInterface.GetIsNetworkAvailable()) return false;

				// 进一步尝试访问一个轻量级的 URL（Google 的 204 响应）
				using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

				// 发起请求
				var _ = client.GetAsync("https://www.google.com/generate_204").Result;

				// 能成功访问则视为在线
				return true;
			}
			catch { return false; }
		}

		// DetectSc2InstallPath 方法检测 SC2 安装路径
		private static string DetectSc2InstallPath()
		{
			// CA1416 规则：仅在 Windows 上运行
			if (!OperatingSystem.IsWindows())
			{
				return string.Empty;
			}

			// 1) 从 Uninstall 键读取 InstallLocation / InstallSource（与你的机器匹配）
			var uninstallKeys = new[]
			{
              // 64 位系统上 32 位程序通常写在 WOW6432Node
		     (RegistryHive.LocalMachine, RegistryView.Registry64,  @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\StarCraft II"),
              // 纯 32 位或非常规安装兜底
		     (RegistryHive.LocalMachine, RegistryView.Registry32,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\StarCraft II"),
		      // 当前用户安装的情况
		     (RegistryHive.CurrentUser,  RegistryView.Default,     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\StarCraft II"),
			};

			foreach (var (hive, view, subKey) in uninstallKeys)
			{
				var p = ReadRegValue(hive, view, subKey, "InstallLocation");
				if (IsValidSc2Path(p)) return p!;
				p = ReadRegValue(hive, view, subKey, "InstallSource");
				if (IsValidSc2Path(p)) return p!;
			}

			// 2) 从 Capabilities\ApplicationIcon 推断（例如："E:\StarCraft II\Support\SC2Switcher.exe",0）
			var capKeys = new[]
			{
		(RegistryHive.LocalMachine, RegistryView.Registry64,  @"SOFTWARE\WOW6432Node\Blizzard Entertainment\StarCraft II\Capabilities"),
		(RegistryHive.LocalMachine, RegistryView.Registry64,  @"SOFTWARE\Blizzard Entertainment\StarCraft II\Capabilities"),
		(RegistryHive.CurrentUser,  RegistryView.Default,     @"SOFTWARE\Blizzard Entertainment\StarCraft II\Capabilities"),
	};
			foreach (var (hive, view, subKey) in capKeys)
			{
				var icon = ReadRegValue(hive, view, subKey, "ApplicationIcon");
				var exe = CleanExePath(icon);      // 去掉引号与逗号索引
				var root = BackToSc2Root(exe);      // 从 Support\*.exe 回退到 “...\StarCraft II”
				if (IsValidSc2Path(root)) return root!;
			}

			// 3) 从文件关联（Classes）推断（与你的 reg query 输出一致）
			string[] classSubKeys =
			{
				@"SOFTWARE\Classes\Blizzard.SC2Map\DefaultIcon",
		        @"SOFTWARE\Classes\Blizzard.SC2Map\shell\open\command",
		        @"SOFTWARE\Classes\Blizzard.SC2Replay\DefaultIcon",
		        @"SOFTWARE\Classes\Blizzard.SC2Replay\shell\open\command",
		        @"SOFTWARE\Classes\Blizzard.SC2Save\DefaultIcon",
		        @"SOFTWARE\Classes\Blizzard.SC2Save\shell\open\command",
			};
			foreach (var sub in classSubKeys)
			{
				var v = ReadRegValue(RegistryHive.LocalMachine, RegistryView.Registry64, sub, null)
					 ?? ReadRegValue(RegistryHive.LocalMachine, RegistryView.Registry32, sub, null);
				var exe = CleanExePath(v);
				var root = BackToSc2Root(exe);
				if (IsValidSc2Path(root)) return root!;
			}

			// 4) 常见默认目录兜底
			var defaults = new[]
			{
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "StarCraft II"),
		        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),     "StarCraft II"),
		        @"C:\Program Files (x86)\StarCraft II",
		        @"C:\Program Files\StarCraft II"
	};
			foreach (var d in defaults)
			{
				if (IsValidSc2Path(d)) return d;
			}

			return string.Empty;
		}

		/// <summary>
		/// 检查路径是否为有效的 SC2 安装目录
		/// </summary>
		/// <param name="path">路径</param>
		/// <returns>true:路径有效/false:路径无效</returns>
		private static bool IsValidSc2Path(string? path)
		{
			if (string.IsNullOrWhiteSpace(path)) return false;
			if (!Directory.Exists(path)) return false;
			var exe = Path.Combine(path, "StarCraft II.exe");
			return File.Exists(exe) || File.Exists(Path.Combine(path, "Support64", "SC2Switcher_x64.exe"));
		}

		/// <summary>
		/// 读取注册表值
		/// </summary>
		/// <param name="hive">注册表匣</param>
		/// <param name="view">注册表视图</param>
		/// <param name="subKey">辅助键</param>
		/// <param name="name">值名</param>
		/// <returns>注册表值，没有则返回null</returns>
		private static string? ReadRegValue(RegistryHive hive, RegistryView view, string subKey, string? name)
		{
			// CA1416 规则：仅在 Windows 上运行
			if (!OperatingSystem.IsWindows())
				return null;

			try
			{
				// 打开注册表键并读取值
				using var baseKey = RegistryKey.OpenBaseKey(hive, view);

				// 打开子键
				using var key = baseKey.OpenSubKey(subKey);

				// 读取值
				if (key == null) return null;

				// 读取指定名称的值，若 name 为空则读取默认值
				var val = name is null ? key.GetValue("") : key.GetValue(name);

				// 返回字符串形式
				return val?.ToString();
			}
			catch { return null; }
		}

		/// <summary>
		/// 清洗注册表中的 EXE 字符串：去掉引号与逗号后的图标索引；
		/// 若是 command 行（含 "%1"），提取第一个 exe 路径。
		/// </summary>
		private static string? CleanExePath(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return null;

			var s = raw.Trim();

			// 去掉逗号后的图标索引：..."exe",2 -> ..."exe"
			var commaIdx = s.LastIndexOf(',');
			if (commaIdx >= 0) s = s.Substring(0, commaIdx);

			// 如果是带参数的 command 行，优先提取第一对引号中的 exe
			if (s.Contains(".exe", StringComparison.OrdinalIgnoreCase))
			{
				var firstQuote = s.IndexOf('"');
				var secondQuote = s.IndexOf('"', firstQuote + 1);
				if (firstQuote >= 0 && secondQuote > firstQuote)
				{
					var candidate = s.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
					if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return candidate;
				}

				// 无引号时按空格切
				var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				var exePart = parts.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
				if (!string.IsNullOrEmpty(exePart)) return exePart.Trim('"');
			}

			// 最后再去掉外围引号
			return s.Trim('"');
		}

		/// <summary>
		/// 从 exe 路径回退到 “...\StarCraft II” 根目录：
		/// - 若是 ...\StarCraft II\StarCraft II.exe => 返回其目录
		/// - 若是 ...\StarCraft II\Support(\64)\SC2Switcher*.exe => 返回上一级（StarCraft II）
		/// </summary>
		private static string? BackToSc2Root(string? exePath)
		{
			if (string.IsNullOrWhiteSpace(exePath)) return null;
			try
			{
				var dir = Path.GetDirectoryName(exePath);
				if (dir == null) return null;

				// 直接就是根目录
				if (File.Exists(Path.Combine(dir, "StarCraft II.exe")))
					return dir;

				// 从 Support/Support64 回退一级
				var parent = Directory.GetParent(dir)?.FullName;
				if (!string.IsNullOrEmpty(parent) &&
					Path.GetFileName(parent).Equals("StarCraft II", StringComparison.OrdinalIgnoreCase))
				{
					return parent;
				}

				// 再兜底回退一级（极少数布局）
				var parent2 = Directory.GetParent(parent ?? "")?.FullName;
				if (!string.IsNullOrEmpty(parent2) &&
					File.Exists(Path.Combine(parent2, "StarCraft II.exe")))
					return parent2;

				return null;
			}
			catch { return null; }
		}

		// ========== 快捷方式创建（纯托管 COM 互操作） ==========

		private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDir, string? iconPath)
		{
			// 使用 IShellLinkW + IPersistFile 创建 .lnk（无需额外引用）
			var link = (IShellLinkW)new ShellLink();
			link.SetPath(targetPath);
			if (!string.IsNullOrWhiteSpace(arguments)) link.SetArguments(arguments);
			if (!string.IsNullOrWhiteSpace(workingDir)) link.SetWorkingDirectory(workingDir);
			if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath)) link.SetIconLocation(iconPath, 0);
			((IPersistFile)link).Save(shortcutPath, true);
		}

		/// <summary>
		/// ShellLink COM 类
		/// </summary>
		[ComImport]
		[Guid("00021401-0000-0000-C000-000000000046")]
		private class ShellLink { }

		/// <summary>
		/// IShellLinkW 接口
		/// </summary>
		[ComImport]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		[Guid("000214F9-0000-0000-C000-000000000046")]
		private interface IShellLinkW
		{
			void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
			void GetIDList(out IntPtr ppidl);
			void SetIDList(IntPtr pidl);
			void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
			void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
			void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
			void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
			void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
			void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
			void GetHotkey(out short pwHotkey);
			void SetHotkey(short wHotkey);
			void GetShowCmd(out int piShowCmd);
			void SetShowCmd(int iShowCmd);
			void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
			void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
			void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
			void Resolve(IntPtr hwnd, uint fFlags);
			void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
		}

		/// <summary>
		/// IPersistFile 接口
		/// </summary>
		[ComImport]
		[Guid("0000010B-0000-0000-C000-000000000046")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IPersistFile
		{
			void GetClassID(out Guid pClassID);
			void IsDirty();
			void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
			void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
			void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
			void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
		}

		// ========== 控制台输出工具 ==========

		/// <summary>
		/// 标题输出
		/// </summary>
		/// <param name="msg">消息内容</param>
		private static void Title(string msg)
		{
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.WriteLine("== " + msg + " ==");
			Console.ResetColor();
		}

		/// <summary>
		/// 信息输出
		/// </summary>
		/// <param name="msg">消息内容</param>
		private static void Info(string msg)
		{
			Console.ForegroundColor = ConsoleColor.Gray;
			Console.WriteLine("[信息] " + msg);
			Console.ResetColor();
		}

		/// <summary>
		/// 警告输出
		/// </summary>
		/// <param name="msg">消息内容</param>
		private static void Warn(string msg)
		{
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine("[警告] " + msg);
			Console.ResetColor();
		}

		/// <summary>
		/// 错误输出
		/// </summary>
		/// <param name="msg">消息内容</param>
		private static void Error(string msg)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine("[错误] " + msg);
			Console.ResetColor();
		}

		/// <summary>
		/// 成功输出
		/// </summary>
		/// <param name="msg">消息内容</param>
		private static void Success(string msg)
		{
			Console.ForegroundColor = ConsoleColor.Green;
			Console.WriteLine("[完成] " + msg);
			Console.ResetColor();
		}

		/// <summary>
		/// 暂停并退出
		/// </summary>
		/// <param name="code">退出代码</param>
		private static void PauseExit(int code)
		{
			Console.WriteLine("按任意键退出……");
			Console.ReadKey(true);
			Environment.Exit(code);
		}

		/// <summary>
		/// 检查当前进程是否以管理员身份运行
		/// </summary>
		/// <returns>true: 是/false: 否 </returns>
		private static bool IsAdmin()
		{
			try
			{
				// CA1416: 仅在 Windows 上运行
				if (!OperatingSystem.IsWindows())
					return false;

				// 读取当前 Windows 身份
				WindowsIdentity identity = WindowsIdentity.GetCurrent();

				// 检查是否为管理员角色
				WindowsPrincipal principal = new WindowsPrincipal(identity);

				// 返回结果
				return principal.IsInRole(WindowsBuiltInRole.Administrator);
			}
			catch { return false; }
		}
	}

	// ========== 配置/Manifest ==========
	internal sealed class InstallerManifest
	{
		public List<string> Packages { get; set; } = new();
	}
}
