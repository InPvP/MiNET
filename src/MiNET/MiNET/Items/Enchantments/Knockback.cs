namespace MiNET.Items.Enchantments
{
	public class Knockback : Enchantment
	{
		public Knockback(short level = 1) : base(level)
		{
			Id = EnchantmentType.Knockback;
		}
	}
}