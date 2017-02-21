namespace MiNET.Items.Enchantments
{
	public class Luck : Enchantment
	{
		public Luck(short level = 1) : base(level)
		{
			Id = EnchantmentType.Luck;
		}
	}
}