namespace MiNET.Items.Enchantments
{
	public class Looting : Enchantment
	{
		public Looting(short level = 1) : base(level)
		{
			Id = EnchantmentType.Looting;
		}
	}
}