using ExcelDb;
using ExcelDb.Loading;
using ExcelDb.Protocol;
using Game.Configs;

namespace SkillEditor.Avalonia.ViewModels;

public static class SampleWorkbookData
{
    public static Dictionary<TableId, TableContent> Create()
    {
        static ExternalRef Ext(string id) => new() { Scheme = "unity", Id = id };
        static RowRef Ref(int table, int id) => new() { Table = table, Id = id };

        return new Dictionary<TableId, TableContent>
        {
            [new TableId(1)] = new TableContent(new List<TableRecord>
            {
                new(1, new CharacterConfig
                {
                    Key = "hero",
                    Name = "Hero",
                    Hp = 100,
                    Icon = Ext("icon_hero"),
                    Skills = { Ref(2, 1) },
                }),
                new(2, new CharacterConfig { Key = "villain", Name = "Villain", Hp = 200 }),
            }),
            [new TableId(2)] = new TableContent(new List<TableRecord>
            {
                new(1, new SkillConfig
                {
                    Key = "fireball",
                    Cost = 10,
                    Payload = Ref(3, 1),
                    Chains = { Ref(2, 2) },
                }),
                new(2, new SkillConfig
                {
                    Key = "icenova",
                    Cost = 20,
                    Chains = { Ref(2, 1) },
                }),
            }),
            [new TableId(3)] = new TableContent(new List<TableRecord>
            {
                new(1, new BuffConfig { Key = "burn", Duration = 3, Vfx = Ext("vfx_burn") }),
            }),
            [new TableId(4)] = new TableContent(new List<TableRecord>
            {
                new(1, new BulletConfig { Key = "pellet", Speed = 42 }),
            }),
            [new TableId(5)] = new TableContent(new List<TableRecord>
            {
                new(1, new LevelConfig { Stage = 1, Difficulty = "easy", HpScale = 100 }),
                new(2, new LevelConfig { Stage = 1, Difficulty = "hard", HpScale = 250, Bosses = { Ref(1, 2) } }),
            }),
            [new TableId(6)] = new TableContent(new List<TableRecord>
            {
                new(1, new BehaviorTreeConfig
                {
                    Key = "guard_ai",
                    Title = "Guard AI",
                    Root = Ref(7, 1),
                }),
            }),
            [new TableId(7)] = new TableContent(new List<TableRecord>
            {
                new(1, new BehaviorNodeConfig
                {
                    Key = "root",
                    DisplayName = "Root Selector",
                    Kind = "Selector",
                    X = 420,
                    Y = 60,
                    OwnerTree = Ref(6, 1),
                    Children = { Ref(7, 2), Ref(7, 5) },
                }),
                new(2, new BehaviorNodeConfig
                {
                    Key = "patrol_sequence",
                    DisplayName = "Patrol",
                    Kind = "Sequence",
                    X = 210,
                    Y = 210,
                    OwnerTree = Ref(6, 1),
                    Children = { Ref(7, 3), Ref(7, 4) },
                }),
                new(3, new BehaviorNodeConfig
                {
                    Key = "has_patrol_route",
                    DisplayName = "Has Route",
                    Kind = "Condition",
                    Action = "HasPatrolRoute",
                    X = 90,
                    Y = 360,
                    OwnerTree = Ref(6, 1),
                }),
                new(4, new BehaviorNodeConfig
                {
                    Key = "move_to_waypoint",
                    DisplayName = "Move To Waypoint",
                    Kind = "Action",
                    Action = "MoveToWaypoint",
                    X = 330,
                    Y = 360,
                    OwnerTree = Ref(6, 1),
                }),
                new(5, new BehaviorNodeConfig
                {
                    Key = "attack_sequence",
                    DisplayName = "Attack",
                    Kind = "Sequence",
                    X = 650,
                    Y = 210,
                    OwnerTree = Ref(6, 1),
                    Children = { Ref(7, 6), Ref(7, 7) },
                }),
                new(6, new BehaviorNodeConfig
                {
                    Key = "can_see_enemy",
                    DisplayName = "Can See Enemy",
                    Kind = "Condition",
                    Action = "CanSeeEnemy",
                    X = 540,
                    Y = 360,
                    OwnerTree = Ref(6, 1),
                }),
                new(7, new BehaviorNodeConfig
                {
                    Key = "attack_target",
                    DisplayName = "Attack Target",
                    Kind = "Action",
                    Action = "AttackTarget",
                    X = 780,
                    Y = 360,
                    OwnerTree = Ref(6, 1),
                }),
            }),
        };
    }
}
