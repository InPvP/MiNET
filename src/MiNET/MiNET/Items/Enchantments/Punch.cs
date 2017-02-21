namespace MiNET.Items.Enchantments
{
	public class Punch : Enchantment
	{
		public Punch(short level = 1) : base(level)
		{
			Id = EnchantmentType.Punch;
		}
	}
}