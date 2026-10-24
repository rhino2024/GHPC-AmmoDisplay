using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GHPC.Player;
using GHPC.Weapons;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
// the assembly also carries a legacy global-namespace AmmoRack, which would win over the import below
using Rack = GHPC.Weapons.AmmoRack;

[assembly: MelonInfo(typeof(AmmoDisplay.AmmoDisplayMod), "弹药架显示", "1.8.1", "rhino2024")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace AmmoDisplay
{
    // 照战争雷霆底部那条状态栏的结构来画：主炮炮弹每种只有一个格子、一个图标，格子右下角的数字是这种弹
    // 全车还剩多少发（弹架上存着的 + 已经在供弹机里的那条弹链），不是只数炮塔里那一架。1.4.0 起默认只剩这一排，
    // 并且整块底板、数字底下的垫黑、上面那行主炮名全部去掉（用户实测「不透明的背景会挡住一部分显示」），数字
    // 改成描边保证在任何底色上都读得出来；机枪弹量收进 ShowMachineGuns 开关，想要再打开。
    //
    // 1.6.0 起主炮那一排里还包含**制导导弹**：ATGM 是车上另一件武器（BMP-2 的 9M113），既不挂在主炮的
    // 待发架上、也不一定列在 LoadoutManager.RackLoadouts 里，所以只按主炮架建行的结果就是用户实测的
    // 「带导弹的机炮车没有 ATGM 图标」。识别用 WeaponSystem.GuidanceUnit，图标一律先试 atgm.png。
    // 图标还先按弹种全名里的制式代号认（APDS-T → apds、APFSDS → apfsds、HEAT → heatfs），因为游戏自己的
    // 短名会粗分：M2 的「25mm APDS-T M791」短名是 Sabot，照短名就套成 apfsds 的图。
    // 1.6.2 起机炮的 APDS-T 改用 apds_belt.png：三发一整条弹链，和单发的 apds.png 区分开。
    // 1.8.1 修：坦克炮的「105mm DM13 APDS-T」全名也带 -T，1.6.2 的判据把它当成机炮弹套了弹链图。
    // 现在按口径分——Caliber 小于 50mm 才算机炮（25mm M791、30mm 2A42 这些），105/120mm 一律单发图。
    // 1.8.0 起主炮那排末尾多一格烟幕弹。烟幕发射器不是 WeaponSystem（玩家按键走的是 VehicleSmokeManager），
    // 所以不在上面两排里，必须单独读全车每个发射管的 SmokeSlot.Rounds 加起来；图标固定用 smoke.png。
    //
    // 全车总数的取法有两批，缺一不可：
    // ① 弹架上还没被取走的：把这辆车每个弹架（LoadoutManager.RackLoadouts 列出来的那些）当前存着的每个
    //    AmmoClip 的 Capacity 按弹种加起来——游戏自己的 GetCurrentStoredAmmoCount 累加的也正是这个数。
    //    不能改成调 LoadoutManager.GetCurrentAmmoCount(clip)：那个方法拿弹架声明的 ClipTypes 去和架上
    //    实际存的 clip 做 AmmoClip.Equals，而 Equals 比的是 MinimalPattern 的长度和内容，两者对不上就
    //    一律返回 0（BMP-2 的 30mm 实测就是全 0）。
    // ② 已经在供弹机里的：AmmoRack.RetrieveClipWithPattern 取弹时会 StoredClips.RemoveAt() 把整架弹从
    //    架上摘走，所以这部分一定数不到①里。机炮（BMP-2 的 2A42）最典型：一整个弹链就是一个 clip，一进场
    //    就全在供弹机里，架上剩 0——只数①就是用户实测的「用机炮时弹药显示为 0」，而游戏 HUD 同一时刻显示
    //    (160 + 0)，那个 160 就是 AmmoFeed 弹链队列的长度（WeaponHud 用的正是 CurrentClipRemainingCount
    //    + ReadyRackReserveCount）。所以这里直接读那两条队列 _feedClipMain/_feedClipAux，一发一个条目。
    //    游戏取弹是「摘下架 → 塞进供弹机」，两批天然不重叠，加起来才是全车剩余。
    //
    // 图标来自游戏目录旁的一个文件夹（默认 Bin/UserData/AmmoIcons），按弹种自己的数据去匹配文件名；
    // 没匹配到的弹种画一块按类别着色的色块加短名，这样不往 mod 里塞任何受版权保护的图也读得懂。
    public sealed class AmmoDisplayMod : MelonMod
    {
        // 一个格子 = 一个弹种（主炮排）或一挺枪（上面那排）
        struct Entry
        {
            public AmmoType Type;     // 取图标和配色
            public string IconKey;    // 有值就直接按文件名取图标（烟幕弹这类没有 AmmoType 的格子）
            public string Weapon;     // 枪排用它标名字，主炮排留空
            public int Total;         // 全车剩余（待发 + 存储）
            public int Ready;         // 其中已经在炮/枪自己那个架上的
        }

        static MelonPreferences_Category cat;
        static MelonPreferences_Entry<bool> eEnabled, eShowSecondary, eDebugLog;
        static MelonPreferences_Entry<KeyCode> eToggle;
        static MelonPreferences_Entry<float> eCellSize, eMarginBottom;
        static MelonPreferences_Entry<string> eIconDirectory, eAnchor;

        static StreamWriter log;

        static readonly List<Entry> shells = new List<Entry>();
        static readonly List<Entry> guns = new List<Entry>();

        // 每次 Refresh 重算的两张按弹种索引的表：全车还剩多少发、其中多少已经在主炮待发架上。
        // 用「弹种 → 发数」而不是「clip → 发数」，因为游戏里同一种弹的 clip 实例在架上和声明表里
        // 不是同一个对象，只有 AmmoType 才是可靠的键。
        static readonly Dictionary<AmmoType, int> totalByType = new Dictionary<AmmoType, int>();
        static readonly Dictionary<AmmoType, int> readyByType = new Dictionary<AmmoType, int>();
        static readonly Dictionary<AmmoType, int> scratch = new Dictionary<AmmoType, int>();
        static string signature = "";
        static readonly Dictionary<AmmoType, Texture2D> iconByType = new Dictionary<AmmoType, Texture2D>();
        static readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>(
            StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> reportedKey = new Dictionary<string, string>();

        static int AmmoRackId;
        static bool showRow;
        static float reloadFraction = -1f;
        static AmmoType selectedType;

        // _clipFeedTime only advances while a reload is running, and TotalReloadTime already carries
        // the damaged-autoloader multiplier, so the two together give a correct 0..1 bar
        static AccessTools.FieldRef<AmmoFeed, float> ClipFeedTimeRef;
        static AccessTools.FieldRef<AmmoFeed, float> RoundFeedTimeRef;

        // 供弹机里那两条弹链（双供弹的机炮是主链 + 副链），Queue 里一发一个条目
        static AccessTools.FieldRef<AmmoFeed, Queue<AmmoType>> MainBeltRef;
        static AccessTools.FieldRef<AmmoFeed, Queue<AmmoType>> AuxBeltRef;

        // 烟幕发射管不在 Weapons 里，发数记在 VehicleSmokeManager 的 _smokeSlots 私有数组上
        static AccessTools.FieldRef<GHPC.Equipment.VehicleSmokeManager,
            GHPC.Equipment.VehicleSmokeManager.SmokeSlot[]> SmokeSlotsRef;

        static GUIStyle tileStyle;
        static GUIStyle countStyle;
        static int countStyleSize = -1;

        public override void OnInitializeMelon()
        {
            cat = MelonPreferences.CreateCategory("AmmoDisplay", "弹药架显示");
            eEnabled = cat.CreateEntry<bool>("Enabled", true,
                "主开关：在屏幕下方显示弹药栏");
            eToggle = cat.CreateEntry<KeyCode>("ToggleKey", KeyCode.F3,
                "任务里开关弹药栏的按键");
            eCellSize = cat.CreateEntry<float>("CellSize", 96f,
                "主炮每个弹种格子的边长（像素），图标就按这个大小画；上面那排机枪格子取它的 0.8 倍");
            eShowSecondary = cat.CreateEntry<bool>("ShowMachineGuns", false,
                "是否额外显示非主炮武器（同轴机枪、车顶枪、烟幕发射器）的剩余弹量。默认关着——主炮那排才是"
                + "要盯着看的，多一排会把画面下方挡得更多");
            eAnchor = cat.CreateEntry<string>("Anchor", "BottomCenter",
                "弹药栏的位置：BottomCenter、BottomLeft 或 BottomRight");
            eMarginBottom = cat.CreateEntry<float>("MarginBottom", 8f,
                "弹药栏离屏幕底边多少像素（是底边到弹链进度条的距离，填小就是贴在最下面）");
            eIconDirectory = cat.CreateEntry<string>("IconDirectory", "UserData/AmmoIcons",
                "你自己的弹种图标文件夹（相对游戏目录或绝对路径）。mod 自带一套图标，这里的同名文件会覆盖它们；"
                + "文件名按弹种匹配，例如 apfsds.png、heat.png、he.png、atgm.png、mg.png");
            eDebugLog = cat.CreateEntry<bool>("DebugLog", false,
                "把每个弹种匹配到哪张图标、每种弹的待发/全车总数是多少写进 Bin/Mods/AmmoDisplay.debug.log，只在图标或数字不对时打开");

            if (eDebugLog.Value) OpenLog();
            ClipFeedTimeRef = TryFieldRef<AmmoFeed>("_clipFeedTime");
            RoundFeedTimeRef = TryFieldRef<AmmoFeed>("_roundFeedTime");
            MainBeltRef = TryBeltRef("_feedClipMain");
            AuxBeltRef = TryBeltRef("_feedClipAux");
            SmokeSlotsRef = TrySmokeSlotsRef();
            LoadIcons();
            MelonLogger.Msg("Ammo Display loaded: " + icons.Count + " icon(s) from " + IconFolder()
                + " (debug log " + (eDebugLog.Value ? "on" : "off") + ")");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            shells.Clear();
            guns.Clear();
            totalByType.Clear();
            readyByType.Clear();
            iconByType.Clear();
            seenMessages.Clear();
            AmmoRackId = 0;
            showRow = false;
            signature = "";
            LoadIcons();
            Log("scene loaded: " + sceneName);
        }

        public override void OnApplicationQuit()
        {
            if (log == null) return;
            try { log.Dispose(); } catch { }
            log = null;
        }

        public override void OnUpdate()
        {
            if (eToggle != null && eToggle.Value != KeyCode.None && Input.GetKeyDown(eToggle.Value))
            {
                eEnabled.Value = !eEnabled.Value;
                MelonLogger.Msg("Ammo Display: " + (eEnabled.Value ? "on" : "off"));
            }

            showRow = false;
            reloadFraction = -1f;
            if (eEnabled == null || !eEnabled.Value) return;
            if (GHPC.AarController.InAar) return;
            try
            {
                Refresh();
            }
            catch (Exception e)
            {
                // this runs every frame, so one line per distinct failure instead of a log per frame
                string message = e.Message;
                if (message != lastError)
                {
                    lastError = message;
                    MelonLogger.Warning("Ammo Display: " + message);
                    Log("refresh threw: " + e);
                }
            }
        }

        static string lastError = "";

        static void Refresh()
        {
            var pi = PlayerInput.Instance;
            if (pi == null) return;
            var brain = pi.CurrentPlayerCrewBrain;
            var module = brain != null ? brain.WeaponsModule : null;
            if (module == null) return;

            var main = MainGun(module);
            if (main == null || main.Weapon == null) return;
            var feed = main.Weapon.Feed;
            if (feed == null) return;
            var rack = feed.ReadyRack;
            if (rack == null) return;

            // 全车弹药总量：逐个弹架把架上现存的 clip 容量按弹种累加
            var own = pi.CurrentPlayerUnit;
            var loadout = own != null ? own.LoadoutManager : null;

            int id = rack.GetInstanceID();
            if (id != AmmoRackId)
            {
                AmmoRackId = id;
                Log("rack changed: " + SafeName(rack) + " clipTypes=" + Length(rack.ClipTypes)
                    + " stored=" + Count(rack.StoredClips)
                    + " racks=" + (loadout != null && loadout.RackLoadouts != null
                        ? loadout.RackLoadouts.Length : -1)
                    + " " + BeltInfo(feed));
            }

            totalByType.Clear();
            readyByType.Clear();
            SumRack(rack, readyByType);
            if (loadout == null) LogOnce("no LoadoutManager on the player unit yet");
            else SumVehicleRacks(loadout, rack, totalByType);
            // 机炮的整条弹链都在供弹机里、弹架空着，只数弹架就会像 BMP-2 的 2A42 那样显示 0
            SumFeeds(module, totalByType);
            if (totalByType.Count == 0) SumRack(rack, totalByType);

            shells.Clear();
            BuildShells(rack, shells);
            // ATGM 是车上另一件武器，弹既不挂在主炮的待发架上、也不一定列在 RackLoadouts 里，
            // 只按主炮架建行就会像 BMP-2 那样一整格导弹都不出现
            BuildMissiles(module, shells);
            BuildSmoke(own, shells);

            guns.Clear();
            if (eShowSecondary != null && eShowSecondary.Value) BuildGuns(module, guns);

            try { selectedType = main.Weapon.CurrentAmmoType; }
            catch { selectedType = null; }
            if (selectedType == null)
            {
                // the game clears the breech round the instant the weapon fires, so the loaded clip
                // is what keeps the highlight from blinking off between shots
                try
                {
                    if (feed.AmmoTypeInBreech != null) selectedType = feed.AmmoTypeInBreech;
                    else selectedType = TypeOf(feed.LoadedClipType);
                }
                catch { }
            }

            reloadFraction = ReloadFraction(feed);
            // 切到导弹发射具时高亮要跟正在用的那件武器，否则打导弹时框还留在炮塔那格上
            try
            {
                var active = module.ActiveWeapon;
                var activeWeapon = active != null ? active.Weapon : null;
                if (activeWeapon != null && activeWeapon.GuidanceUnit != null
                    && activeWeapon.CurrentAmmoType != null)
                    selectedType = activeWeapon.CurrentAmmoType;
            }
            catch { }

            showRow = shells.Count > 0 || guns.Count > 0;

            // 每帧都在跑，所以只有数字真的变了才留一行日志，免得调试日志几秒就上百兆
            var sb = new StringBuilder();
            for (int i = 0; i < shells.Count; i++)
            {
                string label = NameOf(shells[i].Type);
                if (string.IsNullOrEmpty(label)) label = shells[i].IconKey;
                sb.Append(SafeText(label)).Append('=').Append(shells[i].Total)
                  .Append('/').Append(shells[i].Ready).Append(' ');
            }
            for (int i = 0; i < guns.Count; i++)
                sb.Append(SafeText(guns[i].Weapon)).Append('=').Append(guns[i].Total).Append(' ');
            string now = sb.ToString();
            if (now != signature)
            {
                signature = now;
                Log("counts: " + now.Trim());
            }
        }

        static void SumVehicleRacks(GHPC.Weapons.LoadoutManager loadout, Rack readyRack,
            Dictionary<AmmoType, int> into)
        {
            GHPC.Weapons.LoadoutManager.RackLoadout[] racks = null;
            try { racks = loadout.RackLoadouts; } catch (Exception e) { Log("RackLoadouts threw: " + e.Message); }
            if (racks == null || racks.Length == 0)
            {
                LogOnce("LoadoutManager.RackLoadouts is empty: only the ready rack can be counted");
                return;
            }
            for (int i = 0; i < racks.Length; i++)
            {
                var rl = racks[i];
                if (rl == null || rl.Rack == null) continue;
                SumRack(rl.Rack, into);
                LogOnce("rack " + i + " [" + SafeText(rl.Name) + "] stored="
                    + Count(rl.Rack.StoredClips) + (rl.Rack == readyRack ? " (ready)" : ""));
            }
        }

        // 一个弹架现存的所有 clip，按弹种把 Capacity 累加进 into。Clip 打掉的发数游戏自己会减
        // Capacity（它自己的 GetCurrentStoredAmmoCount 累加的就是这个数），所以这里不用做别的修正。
        static void SumRack(Rack rack, Dictionary<AmmoType, int> into)
        {
            if (rack == null) return;
            var clips = SafeList(rack.StoredClips);
            for (int i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                if (clip == null) continue;
                AmmoType type = TypeOf(clip);
                if (type == null) continue;
                int rounds = 0;
                try { rounds = clip.Capacity; } catch { rounds = 0; }
                if (rounds < 0) rounds = 0;
                int had;
                into.TryGetValue(type, out had);
                into[type] = had + rounds;
            }
        }

        // 供弹机里已经上炮、但还没打出去的那些发：游戏取弹时把整架弹从 StoredClips 里 RemoveAt 掉了，
        // 所以这批在弹架上一定数不到，必须单独加。弹链队列是一发一个条目，直接按弹种点数最准
        // （双供弹机炮的主链和副链是两条，各是一种弹，所以两种都要）。
        static void SumFeeds(GHPC.Crew.CrewBrainWeaponsModule module, Dictionary<AmmoType, int> into)
        {
            List<WeaponSystemInfo> list = null;
            try { list = module.Weapons; } catch { list = null; }
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                SumFeed(w != null ? w.Weapon != null ? w.Weapon.Feed : null : null, into);
            }
        }

        static void SumFeed(AmmoFeed feed, Dictionary<AmmoType, int> into)
        {
            if (feed == null || MainBeltRef == null) return;
            try
            {
                AddBelt(MainBeltRef(feed), into);
                if (AuxBeltRef != null) AddBelt(AuxBeltRef(feed), into);
                AddRound(into, feed.AmmoTypeInBreech);
            }
            catch (Exception e) { LogOnce("feed belt threw: " + e.Message); }
        }

        static void AddBelt(Queue<AmmoType> belt, Dictionary<AmmoType, int> into)
        {
            if (belt == null) return;
            // Queue 的枚举器是结构体，不走接口，所以每帧过一条 160 发的弹链也不产生垃圾
            foreach (var type in belt) AddRound(into, type);
        }

        static void AddRound(Dictionary<AmmoType, int> into, AmmoType type)
        {
            if (type == null) return;
            int had;
            into.TryGetValue(type, out had);
            into[type] = had + 1;
        }

        // 调试用：说清数字到底来自弹架还是来自弹链，下次实测一眼能判
        static string BeltInfo(AmmoFeed feed)
        {
            if (feed == null || MainBeltRef == null) return "belt=n/a";
            try
            {
                var main = MainBeltRef(feed);
                var aux = AuxBeltRef != null ? AuxBeltRef(feed) : null;
                return "beltMain=" + (main != null ? main.Count.ToString() : "-")
                    + " beltAux=" + (aux != null ? aux.Count.ToString() : "-")
                    + " breech=" + SafeText(NameOf(feed.AmmoTypeInBreech));
            }
            catch (Exception e) { return "belt threw " + e.Message; }
        }

        static WeaponSystemInfo MainGun(GHPC.Crew.CrewBrainWeaponsModule module)
        {
            List<WeaponSystemInfo> list = null;
            try { list = module.Weapons; } catch { list = null; }
            if (list == null) return null;

            WeaponSystemInfo fallback = null;
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w == null || w.Weapon == null || w.Weapon.Feed == null) continue;
                if (w.Role == WeaponSystemRole.MainGun) return w;
                if (fallback == null) fallback = w;
            }
            return fallback;
        }

        // 主炮那一排 = 这门炮能装的每个弹种一格。ClipTypes 是弹架自己声明的可装弹种（打空了也还在里面，
        // 所以剩 0 的弹种照样占一格，跟战雷一致）；个别车没填它就退回当前实际存着的弹种。
        // 数字一律从 totalByType/readyByType 取，不再拿 clip 实例去问游戏——见文件顶部的 Equals 说明。
        static void BuildShells(Rack rack, List<Entry> into)
        {
            var types = TypesOf(rack.ClipTypes);
            if (types.Count == 0) types = TypesOf(SafeList(rack.StoredClips));

            for (int i = 0; i < types.Count; i++)
            {
                var type = types[i];
                int total = Get(totalByType, type);
                int ready = Get(readyByType, type);
                if (total < ready) total = ready;
                into.Add(new Entry { Type = type, Weapon = null, Total = total, Ready = ready });
            }
        }

        // 上面那一排 = 这辆车除主炮之外、自己有弹架的武器（同轴机枪、车顶枪、烟幕发射器…）
        static void BuildGuns(GHPC.Crew.CrewBrainWeaponsModule module, List<Entry> into)
        {
            List<WeaponSystemInfo> list = null;
            try { list = module.Weapons; } catch { list = null; }
            if (list == null) return;

            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w == null || w.Role == WeaponSystemRole.MainGun) continue;
                var feed = w.Weapon != null ? w.Weapon.Feed : null;
                var rack = feed != null ? feed.ReadyRack : null;
                if (rack == null) continue;

                scratch.Clear();
                SumRack(rack, scratch);
                var types = TypesOf(rack.ClipTypes);
                if (types.Count == 0) types = TypesOf(SafeList(rack.StoredClips));

                for (int j = 0; j < types.Count; j++)
                {
                    var type = types[j];
                    int inRack = Get(scratch, type);
                    int total = Get(totalByType, type);
                    if (total < inRack) total = inRack;
                    if (total <= 0) continue;
                    into.Add(new Entry
                    {
                        Type = type,
                        // 个别车的烟幕发射器也可能挂成 WeaponSystem，按角色直接认成烟幕图标
                        IconKey = w.Role == WeaponSystemRole.SmokeLauncher ? "smoke" : null,
                        Weapon = string.IsNullOrEmpty(w.Name) ? w.Role.ToString() : w.Name,
                        Total = total,
                        Ready = inRack
                    });
                }
            }
        }

        // 烟幕弹一格：玩家按 G 打的是 VehicleSmokeManager，不是 Weapons 里的 WeaponSystemInfo，
        // 所以上面两排都收不到它。每个发射管的 Rounds 加起来就是全车还剩几发（打一发游戏自己减）。
        static void BuildSmoke(GHPC.Unit unit, List<Entry> into)
        {
            if (unit == null || SmokeSlotsRef == null) return;
            var broker = unit.InfoBroker;
            var smoke = broker != null ? broker.VehicleSmokeManager : null;
            if (smoke == null) return;

            bool hasLauncher;
            try { hasLauncher = smoke.HasSmokeLauncher; }
            catch { return; }
            if (!hasLauncher) return;

            var slots = SmokeSlotsRef(smoke);
            if (slots == null || slots.Length == 0) return;
            int rounds = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot != null && slot.Rounds > 0) rounds += slot.Rounds;
            }
            into.Add(new Entry { Type = null, IconKey = "smoke", Total = rounds, Ready = rounds });
            LogOnce("smoke launcher: slots=" + slots.Length + " rounds=" + rounds);
        }

        // 主炮那一排之外，还要把制导导弹补进来。识别用 WeaponSystem.GuidanceUnit（游戏自己也读它：
        // UnitInfoBroker 里就是 get_MissileGuidance + MissileGuidanceUnit.CurrentMissiles）。数量有三个
        // 来源，取最大而不是相加：①全车弹架（RackLoadouts 列出来的那些）②这件武器自己的待发架
        // ③发射具上现挂的导弹。游戏取弹是「从架上摘下来 → 塞进供弹机/上膛」，同一发可能同时出现在
        // 两个来源里，相加会虚高，取最大只会偏低不会凭空多弹。
        static void BuildMissiles(GHPC.Crew.CrewBrainWeaponsModule module, List<Entry> into)
        {
            List<WeaponSystemInfo> list = null;
            try { list = module.Weapons; } catch { list = null; }
            if (list == null) return;

            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                var weapon = w != null ? w.Weapon : null;
                GHPC.Weapons.MissileGuidanceUnit guidance = null;
                try { guidance = weapon != null ? weapon.GuidanceUnit : null; } catch { guidance = null; }
                if (guidance == null) continue;

                var rack = weapon.Feed != null ? weapon.Feed.ReadyRack : null;
                scratch.Clear();
                if (rack != null) SumRack(rack, scratch);
                int rails = 0;
                try { rails = guidance.CurrentMissiles != null ? guidance.CurrentMissiles.Count : 0; }
                catch { rails = 0; }

                var types = TypesOf(rack != null ? rack.ClipTypes : null);
                if (types.Count == 0) types = TypesOf(rack != null ? SafeList(rack.StoredClips) : null);
                if (types.Count == 0)
                {
                    AmmoType loaded = null;
                    try { loaded = weapon.CurrentAmmoType; } catch { loaded = null; }
                    if (loaded != null) types.Add(loaded);
                }

                for (int j = 0; j < types.Count; j++)
                {
                    var type = types[j];
                    // 炮射导弹（T-72 的 9M119）本来就在主炮那排里，别再占一格
                    if (type == null || Has(into, type)) continue;
                    int stored = Get(scratch, type);
                    int total = Get(totalByType, type);
                    if (total < stored) total = stored;
                    if (total < rails) total = rails;
                    into.Add(new Entry { Type = type, Weapon = null, Total = total, Ready = stored });
                    LogOnce("missile cell [" + SafeText(NameOf(type)) + "] weapon="
                        + SafeText(w.Name) + " rack=" + SafeName(rack) + " stored=" + stored
                        + " rails=" + rails + " vehicleRacks=" + Get(totalByType, type));
                }
            }
        }

        static bool Has(List<Entry> entries, AmmoType type)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Type == type) return true;
            return false;
        }

        static List<AmmoType> TypesOf(IList<AmmoType.AmmoClip> clips)
        {
            var list = new List<AmmoType>();
            if (clips == null) return list;
            for (int i = 0; i < clips.Count; i++)
            {
                AmmoType type = TypeOf(clips[i]);
                if (type != null && !list.Contains(type)) list.Add(type);
            }
            return list;
        }

        static int Get(Dictionary<AmmoType, int> map, AmmoType type)
        {
            int value;
            return type != null && map.TryGetValue(type, out value) ? value : 0;
        }

        static AmmoType TypeOf(AmmoType.AmmoClip clip)
        {
            var pattern = clip.MinimalPattern;
            if (pattern == null || pattern.Length == 0) return null;
            var first = pattern[0];
            return first != null ? first.AmmoType : null;
        }

        static float ReloadFraction(AmmoFeed feed)
        {
            try
            {
                if (feed.Reloading && ClipFeedTimeRef != null && feed.TotalReloadTime > 0.01f)
                    return Mathf.Clamp01(ClipFeedTimeRef(feed) / feed.TotalReloadTime);
                if (feed.Cycling && RoundFeedTimeRef != null && feed.TotalCycleTime > 0.01f)
                    return Mathf.Clamp01(RoundFeedTimeRef(feed) / feed.TotalCycleTime);
            }
            catch (Exception e) { Log("reload fraction threw: " + e.Message); }
            return -1f;
        }

        public override void OnGUI()
        {
            if (!showRow || Event.current.type != EventType.Repaint) return;

            var matrix = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;

            float size = Mathf.Clamp(eCellSize != null ? eCellSize.Value : 96f, 18f, 200f);
            float secSize = Mathf.Round(size * 0.8f);
            float gap = Mathf.Max(2f, size * 0.1f);
            float pad = Mathf.Max(4f, size * 0.1f);
            float rowGap = Mathf.Max(3f, size * 0.12f);
            float margin = Mathf.Clamp(eMarginBottom != null ? eMarginBottom.Value : 8f, 0f, 2000f);
            float labelWidth = Mathf.Max(22f, secSize * 0.75f);
            int gunFont = Mathf.Max(11, Mathf.RoundToInt(secSize * 0.36f));
            int shellFont = Mathf.Max(12, Mathf.RoundToInt(size * 0.34f));
            // 数字框只当垂直中心的参照：框矮一些、贴着图标下沿，字就不会飘到图标中间；
            // 行高比框高，所以 Count() 里放开裁剪，字照样画得完整
            float countHeight = Mathf.Max(12f, Mathf.Ceil(Mathf.Max(gunFont, shellFont) * 0.9f));

            float shellWidth = shells.Count > 0 ? (size + gap) * shells.Count - gap : 0f;
            // 机枪那排：一个格子 + 紧跟其后的数字
            float gunWidth = guns.Count > 0
                ? (secSize + 2f + labelWidth + gap) * guns.Count - gap : 0f;
            float widest = Mathf.Max(shellWidth, gunWidth);
            if (widest <= 0f) { GUI.matrix = matrix; return; }

            float panelWidth = widest + pad * 2f;
            float panelHeight = pad * 0.4f
                + (guns.Count > 0 ? secSize + rowGap : 0f)
                + (shells.Count > 0 ? size : 0f)
                + pad * 0.6f
                + (reloadFraction >= 0f ? 6f : 0f);

            float panelX = (Screen.width - panelWidth) * 0.5f;
            string anchor = eAnchor != null ? eAnchor.Value : null;
            if (!string.IsNullOrEmpty(anchor))
            {
                if (anchor.Equals("BottomLeft", StringComparison.OrdinalIgnoreCase)) panelX = 14f;
                else if (anchor.Equals("BottomRight", StringComparison.OrdinalIgnoreCase))
                    panelX = Screen.width - 14f - panelWidth;
            }
            panelX = Mathf.Clamp(panelX, 4f, Mathf.Max(4f, Screen.width - panelWidth - 4f));
            float panelY = Screen.height - margin - panelHeight;

            // 整块底板不画了：图标自带底纹，数字改成描边，垫黑只会挡住画面（主炮名也一并去掉）
            float y = panelY + pad * 0.4f;

            if (guns.Count > 0)
            {
                float x = panelX + pad;
                for (int i = 0; i < guns.Count; i++)
                {
                    var e = guns[i];
                    var rect = new Rect(x, y, secSize, secSize);
                    DrawArtwork(rect, e, secSize, 1f);
                    DrawCount(new Rect(rect.xMax + 2f, rect.yMax - countHeight, labelWidth, countHeight),
                        e.Total.ToString(), gunFont);
                    x += rect.width + 2f + labelWidth + gap;
                }
                y += secSize + rowGap;
            }

            if (shells.Count > 0)
            {
                float x = panelX + pad;
                for (int i = 0; i < shells.Count; i++)
                {
                    var e = shells[i];
                    var rect = new Rect(x, y, size, size);
                    bool selected = e.Type != null && e.Type == selectedType;
                    DrawArtwork(rect, e, size, e.Total > 0 ? 1f : 0.3f);
                    // 战雷只框住当前选中的那一种弹，不会一框一整排
                    if (selected && e.Total > 0) DrawFrame(rect, new Color(1f, 1f, 1f, 0.8f), 1f);
                    DrawCount(new Rect(rect.x + 2f, rect.yMax - countHeight, rect.width - 4f, countHeight),
                        e.Total.ToString(), shellFont);
                    x += size + gap;
                }
                y += size;
            }

            if (reloadFraction >= 0f)
            {
                var bar = new Rect(panelX + pad, y + 2f, panelWidth - pad * 2f, 3f);
                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.DrawTexture(bar, Texture2D.whiteTexture);
                GUI.color = new Color(0.85f, 0.72f, 0.25f, 0.95f);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * reloadFraction, bar.height),
                    Texture2D.whiteTexture);
                GUI.color = Color.white;
            }

            GUI.matrix = matrix;
        }

        // 图标（没有图标就按弹种类别上色块 + 短名）；dim < 1 用来把打空的弹种压进背景
        static void DrawArtwork(Rect rect, Entry entry, float size, float dim)
        {
            var type = entry.Type;
            var icon = IconFor(entry);
            if (icon != null)
            {
                GUI.color = new Color(dim, dim, dim, 1f);
                GUI.DrawTexture(rect, icon);
            }
            else
            {
                GUI.color = new Color(dim, dim, dim, 1f) * TileColor(type);
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = new Color(dim, dim, dim, 1f);
                GUI.Label(rect, Code(type), Tile(size));
            }
            GUI.color = Color.white;
        }

        // 数字压在图标上，背景必须是透明的（垫黑块会挡住画面），所以改成白字四向描黑边：
        // 浅色图标、草地、天空上都照样读得出来
        static void DrawCount(Rect rect, string text, int fontSize)
        {
            var style = Count(fontSize);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(Offset(rect, 1f, 1f), text, style);
            GUI.Label(Offset(rect, -1f, 1f), text, style);
            GUI.Label(Offset(rect, 1f, -1f), text, style);
            GUI.Label(Offset(rect, -1f, -1f), text, style);
            GUI.color = Color.white;
            GUI.Label(rect, text, style);
        }

        static Rect Offset(Rect rect, float x, float y)
        {
            return new Rect(rect.x + x, rect.y + y, rect.width, rect.height);
        }

        static void DrawFrame(Rect rect, Color color, float thickness)
        {
            var t = Texture2D.whiteTexture;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x - thickness, rect.y - thickness,
                rect.width + thickness * 2f, thickness), t);
            GUI.DrawTexture(new Rect(rect.x - thickness, rect.yMax,
                rect.width + thickness * 2f, thickness), t);
            GUI.DrawTexture(new Rect(rect.x - thickness, rect.y - thickness,
                thickness, rect.height + thickness * 2f), t);
            GUI.DrawTexture(new Rect(rect.xMax, rect.y - thickness,
                thickness, rect.height + thickness * 2f), t);
            GUI.color = Color.white;
        }

        // Icon names are matched against the round's own type data instead of a hardcoded table, so a
        // new shell only needs a file with one of these names dropped into the folder.
        // 有 IconKey 的格子（烟幕弹）直接按文件名取，不再走弹种匹配
        static Texture2D IconFor(Entry entry)
        {
            if (!string.IsNullOrEmpty(entry.IconKey))
            {
                Texture2D keyed;
                if (icons.TryGetValue(entry.IconKey, out keyed)) return keyed;
            }
            return IconFor(entry.Type);
        }

        static Texture2D IconFor(AmmoType type)
        {
            if (type == null) return null;
            Texture2D cached;
            if (iconByType.TryGetValue(type, out cached)) return cached;

            var candidates = Candidates(type);
            Texture2D found = null;
            for (int i = 0; i < candidates.Count; i++)
                if (icons.TryGetValue(candidates[i], out found)) break;

            iconByType[type] = found;
            string key = type.Name + "|" + type.ShortName + "|" + type.VisualType;
            string previous;
            if (!reportedKey.TryGetValue(key, out previous) || previous != (found != null ? "y" : "n"))
            {
                reportedKey[key] = found != null ? "y" : "n";
                Log("icon " + (found != null ? "matched" : "MISSING") + " for ["
                    + SafeText(type.Name) + "] short=" + type.ShortName + " visual=" + type.VisualType
                    + " category=" + type.Category + " guidance=" + type.Guidance
                    + " caliber=" + CaliberMm(type).ToString("0.#") + "mm"
                    + " tried=" + string.Join(",", candidates.ToArray())
                    + " -> " + (found != null ? found.name : "-"));
            }
            return found;
        }

        // 弹种名和图标文件名不一定说同一种话，所以两边各给一次跳转的机会。
        // 顺序按「制导弹 → 全名里的制式代号 → 弹种短名 → 类别 → 视觉类型 → 全名」：短名最贴战雷的图标分类，
        // 但会被游戏自己的粗分类带偏（M2 的 APDS-T 短名是 Sabot），而 VisualType 更是会骗人
        // （3UOR6 HE-T 的 VisualType 是 APDS，先试它就给燃烧弹套上了穿甲弹的图）。
        static List<string> Candidates(AmmoType type)
        {
            var list = new List<string>();
            // 制导弹先试 atgm：各车的短名五花八门，但游戏自己用 Guidance 标过它是不是导弹，比短名可靠
            if (type.Guidance != AmmoType.GuidanceType.Unguided) Add(list, "atgm");
            // 全名里的制式代号比短名具体：M2 的「25mm APDS-T M791」短名是 Sabot，照短名会去套 apfsds 的图
            string token = NameToken(type.Name);
            // 机炮车打的是弹链里的曳光脱壳穿甲弹，用三发一条链的图；主炮的 APDS（DM13 全名也带 -T）仍是一发一发的图
            if (token == "apds" && IsBeltFedApds(type)) Add(list, "apds_belt");
            Add(list, token);
            Add(list, Alias(token));
            string shortKey = Slug(type.ShortName.ToString());
            Add(list, shortKey);
            Add(list, Alias(shortKey));
            string category = Slug(type.Category.ToString());
            Add(list, category);
            Add(list, Alias(category));
            string visual = Slug(type.VisualType.ToString());
            Add(list, visual);
            Add(list, Alias(visual));
            Add(list, Slug(type.Name));
            Add(list, "default");
            return list;
        }

        static void Add(List<string> list, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!list.Contains(key)) list.Add(key);
        }

        // 从弹种全名里认制式代号。只列这三个：APFSDS 里不含「apds」这个子串，所以先长后短不会认错。
        static string NameToken(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string n = name.ToLowerInvariant();
            if (n.Contains("smoke")) return "smoke";
            if (n.Contains("apfsds")) return "apfsds";
            if (n.Contains("apds")) return "apds";
            if (n.Contains("heat")) return "heat";
            return "";
        }

        // 机炮的 APDS-T（25mm M791、30mm 2A42）用三发弹链图；坦克炮的 APDS-T（105mm DM13）全名也带 -T，
        // 所以不能看名字，只能看口径。Caliber 是毫米，0 的弹种按 Radius（米）×2000 换算成毫米
        static bool IsBeltFedApds(AmmoType type)
        {
            float caliber = CaliberMm(type);
            return caliber > 0f && caliber < 50f;
        }

        static float CaliberMm(AmmoType type)
        {
            if (type == null) return 0f;
            float caliber = type.Caliber;
            if (caliber > 0f) return caliber;
            float radius = type.Radius;
            return radius > 0f ? radius * 2000f : 0f;
        }

        // the shell type names in the game and in the icon folder do not always agree, so each side
        // gets one hop to the other
        static string Alias(string key)
        {
            switch (key)
            {
                case "sabot": return "apfsds";
                case "missile": return "atgm";
                case "ap": return "apds";
                case "penetrator": return "apds";
                case "shapedcharge": return "heat";
                case "explosive": return "he";
                case "multipurpose": return "he";
                case "caliber50": return "mg";
                case "coax": return "mg";
                case "bullet": return "ap";
                case "shell": return "he";
                case "spall": return "he";
                case "heat": return "heatfs";
                case "smoke": return "smoke";
                case "smokegrenade": return "smoke";
                case "smokescreen": return "smoke";
                case "smokelauncher": return "smoke";
                default: return "";
            }
        }

        static string Slug(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 'a' && c <= 'z') sb.Append(c);
                else if (c >= 'A' && c <= 'Z') sb.Append(char.ToLowerInvariant(c));
                else if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static Color TileColor(AmmoType type)
        {
            if (type == null) return new Color(0.5f, 0.5f, 0.5f, 0.9f);
            if (type.Guidance != AmmoType.GuidanceType.Unguided) return new Color(0.25f, 0.5f, 0.75f, 0.9f);
            switch (type.Category)
            {
                case AmmoType.AmmoCategory.Penetrator: return new Color(0.45f, 0.5f, 0.58f, 0.9f);
                case AmmoType.AmmoCategory.ShapedCharge: return new Color(0.7f, 0.45f, 0.2f, 0.9f);
                case AmmoType.AmmoCategory.Explosive: return new Color(0.65f, 0.25f, 0.2f, 0.9f);
                default: return new Color(0.5f, 0.5f, 0.5f, 0.9f);
            }
        }

        static string Code(AmmoType type)
        {
            if (type == null) return "?";
            string shortName = type.ShortName.ToString();
            return shortName.Length <= 4 ? shortName : shortName.Substring(0, 4);
        }

        static string NameOf(AmmoType type)
        {
            return type != null ? type.Name : null;
        }

        static GUIStyle Tile(float size)
        {
            int fontSize = Mathf.Max(7, Mathf.RoundToInt(size * 0.3f));
            if (tileStyle != null && tileStyle.fontSize == fontSize) return tileStyle;
            tileStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = fontSize,
                fontStyle = FontStyle.Bold
            };
            tileStyle.normal.textColor = Color.white;
            return tileStyle;
        }

        static GUIStyle Count(int fontSize)
        {
            if (countStyle != null && countStyleSize == fontSize) return countStyle;
            countStyleSize = fontSize;
            countStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleRight,
                fontSize = fontSize,
                fontStyle = FontStyle.Bold,
                clipping = TextClipping.Overflow
            };
            countStyle.normal.textColor = Color.white;
            return countStyle;
        }

        static void LoadIcons()
        {
            icons.Clear();
            iconByType.Clear();
            reportedKey.Clear();
            // 随 mod 发布的图标先加载，用户图标夹最后加载 => 同名文件以用户自己的为准
            int bundled = LoadIconFolder(BundledIconFolder(), false);
            int custom = LoadIconFolder(IconFolder(), true);
            Log("icons loaded: " + icons.Count + " (bundled " + bundled + ", custom " + custom + ")");
        }

        static int LoadIconFolder(string dir, bool warnIfMissing)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                if (warnIfMissing)
                    MelonLogger.Msg("Ammo Display: no icon folder at " + dir
                        + ", using the icons shipped with the mod");
                return 0;
            }

            int loaded = 0;
            try
            {
                var files = Directory.GetFiles(dir, "*.png");
                Array.Sort(files);
                for (int i = 0; i < files.Length; i++)
                {
                    string key = Path.GetFileNameWithoutExtension(files[i]);
                    if (key.StartsWith("_", StringComparison.Ordinal)) continue;
                    try
                    {
                        var bytes = File.ReadAllBytes(files[i]);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        ImageConversion.LoadImage(tex, bytes);
                        tex.name = key;
                        icons[key] = tex;
                        loaded++;
                    }
                    catch (Exception e) { Log("icon " + key + " failed to load: " + e.Message); }
                }
                Log("icons from " + dir + ": " + loaded);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Ammo Display: icon folder threw: " + e.Message);
            }
            return loaded;
        }

        // 图标就放在 AmmoDisplay.dll 旁边（AmmoDisplayIcons），随压缩包一起安装；
        // 不走 UserData/AmmoIcons 是为了兼容管理器只解压 Mods 目录的安装方式
        static string BundledIconFolder()
        {
            try
            {
                string asmDir = Path.GetDirectoryName(typeof(AmmoDisplayMod).Assembly.Location);
                return string.IsNullOrEmpty(asmDir) ? null : Path.Combine(asmDir, "AmmoDisplayIcons");
            }
            catch { return null; }
        }

        static string IconFolder()
        {
            string configured = eIconDirectory != null ? eIconDirectory.Value : null;
            if (string.IsNullOrEmpty(configured)) configured = "UserData/AmmoIcons";
            if (Path.IsPathRooted(configured)) return configured;
            return Path.Combine(Path.GetDirectoryName(Application.dataPath),
                configured.Replace('/', Path.DirectorySeparatorChar));
        }

        static AccessTools.FieldRef<AmmoFeed, Queue<AmmoType>> TryBeltRef(string fieldName)
        {
            try { return AccessTools.FieldRefAccess<AmmoFeed, Queue<AmmoType>>(fieldName); }
            catch (Exception e)
            {
                Log("field " + fieldName + " unavailable: " + e.Message);
                return null;
            }
        }

        static AccessTools.FieldRef<GHPC.Equipment.VehicleSmokeManager,
            GHPC.Equipment.VehicleSmokeManager.SmokeSlot[]> TrySmokeSlotsRef()
        {
            try
            {
                return AccessTools.FieldRefAccess<GHPC.Equipment.VehicleSmokeManager,
                    GHPC.Equipment.VehicleSmokeManager.SmokeSlot[]>("_smokeSlots");
            }
            catch (Exception e)
            {
                Log("field _smokeSlots unavailable: " + e.Message);
                return null;
            }
        }

        static AccessTools.FieldRef<T, float> TryFieldRef<T>(string fieldName)
        {
            try { return AccessTools.FieldRefAccess<T, float>(fieldName); }
            catch (Exception e)
            {
                Log("field " + fieldName + " unavailable: " + e.Message);
                return null;
            }
        }

        static int Length(AmmoType.AmmoClip[] clips)
        {
            try { return clips != null ? clips.Length : 0; }
            catch { return 0; }
        }

        static int Count(IList<AmmoType.AmmoClip> clips)
        {
            return clips == null ? -1 : clips.Count;
        }

        // StoredClips 是运行时列表，取不到就当没有；只留还能认出弹种的条目
        static List<AmmoType.AmmoClip> SafeList(IList<AmmoType.AmmoClip> clips)
        {
            var list = new List<AmmoType.AmmoClip>();
            try
            {
                if (clips == null) return list;
                for (int i = 0; i < clips.Count; i++)
                {
                    var clip = clips[i];
                    if (clip != null && TypeOf(clip) != null) list.Add(clip);
                }
            }
            catch { }
            return list;
        }

        static string SafeName(object o)
        {
            try { return o == null ? "null" : o.GetType().Name + ":" + ((UnityEngine.Object)o).name; }
            catch { return "null"; }
        }

        static string SafeText(string text)
        {
            return string.IsNullOrEmpty(text) ? "-" : text;
        }

        static readonly HashSet<string> seenMessages = new HashSet<string>();

        static void LogOnce(string message)
        {
            // 每帧都在跑，所以每条不同的话只留一次（换车、换关时才清），不能只记「上一条」
            if (seenMessages.Contains(message)) return;
            seenMessages.Add(message);
            Log(message);
        }

        static void OpenLog()
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Mods");
                log = new StreamWriter(Path.Combine(dir, "AmmoDisplay.debug.log"), true,
                    new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch { log = null; }
        }

        internal static void Log(object message)
        {
            if (log == null) return;
            try { log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + message); }
            catch { }
        }
    }
}
