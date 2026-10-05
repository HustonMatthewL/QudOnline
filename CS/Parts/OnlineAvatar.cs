using System;

namespace XRL.World.Parts
{
    // Marks the stand-in for another player's character: a copy of their body placed where they stand. The real
    // character lives in that player's own game, so the copy never takes a turn (refused the way vanilla
    // ArtificialIntelligence refuses the turn of a disabled robot). A hit is passed on to that player's game,
    // which applies it to the real character. The copy takes it too, so the attacker's game shows its usual
    // message, but never enough to die: a dead copy would drop a second set of that player's belongings. Its
    // hit points are set back to the real character's from presence. Avatars are
    // made and removed by QudOnline.OnlineAvatars and are never stored with a zone or a save.
    [Serializable]
    public class OnlineAvatar : IPart
    {
        // The game ID of the player this stands for.
        public string PlayerID;

        public override bool WantEvent(int ID, int cascade)
        {
            return base.WantEvent(ID, cascade) || ID == SingletonEvent<BeginTakeActionEvent>.ID
                || ID == BeforeApplyDamageEvent.ID;
        }

        public override bool HandleEvent(BeginTakeActionEvent E)
        {
            return false;
        }

        public override bool HandleEvent(BeforeApplyDamageEvent E)
        {
            QudOnline.OnlineAvatars.Damaged(PlayerID, E.Damage, E.Actor);
            if (E.Damage == null)
            {
                return false;
            }
            int most = ParentObject.hitpoints - 1;
            if (most <= 0)
            {
                return false;
            }
            if (E.Damage.Amount > most)
            {
                E.Damage.Amount = most;
            }
            return base.HandleEvent(E);
        }
    }
}
