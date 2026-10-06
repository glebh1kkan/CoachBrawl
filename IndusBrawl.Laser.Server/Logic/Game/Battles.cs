namespace IndusBrawl.Laser.Server.Logic.Game
{
    using IndusBrawl.Laser.Logic.Battle;
    using System.Collections.Concurrent;

    public static class Battles
    {
        private static long m_battleIdCounter;
        private static ConcurrentDictionary<long, BattleMode> m_battles;

        public static void Init()
        {
            m_battles = new ConcurrentDictionary<long, BattleMode>();
            m_battleIdCounter = 0;

            new Thread(Update).Start();
        }

        public static void Update()
        {
            while (true)
            {
                foreach (BattleMode battle in m_battles.Values.ToArray())
                {
                    if (battle.IsGameOver)
                    {
                        m_battles.Remove(battle.Id, out _);
                    }
                }
                Thread.Sleep(1000);
            }
        }

        public static long Add(BattleMode battle)
        {
            long id = ++m_battleIdCounter;
            m_battles[id] = battle;
            return id;
        }

        public static BattleMode Get(long id)
        {
            if (!m_battles.ContainsKey(id)) return null;
            return m_battles[id];
        }

        // для тестов: найти бой и персонажа по аккаунту
        public static object FindPlayer(long accountId)
        {
            try
            {
                foreach (BattleMode battle in m_battles.Values.ToArray())
                {
                    if (battle?.m_players == null) continue;
                    foreach (var p in battle.m_players)
                    {
                        if (p == null || p.AccountId != accountId) continue;
                        int x = -1, y = -1, hp = -1;
                        bool alive = p.IsAlive;
                        try
                        {
                            var obj = battle.GetGameObjectManager()?.GetGameObjectByID(p.OwnObjectId);
                            if (obj != null)
                            {
                                x = obj.GetX(); y = obj.GetY();
                                if (obj is IndusBrawl.Laser.Logic.Battle.Objects.Character ch)
                                    hp = ch.m_hitpoints;
                            }
                        }
                        catch { }
                        return new
                        {
                            battleId = battle.Id,
                            tick = battle.GetTicksGone(),
                            team = p.TeamIndex,
                            alive,
                            x, y, hp
                        };
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
