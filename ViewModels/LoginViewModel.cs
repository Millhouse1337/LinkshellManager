using System.ComponentModel.DataAnnotations;

namespace LinkshellManager.ViewModels
{
    public class LoginViewModel
    {
        [Display(Name = "Email Address")]
        [Required(ErrorMessage = "Email address is required")]
        public string EmailAddress { get; set; }
        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; }

        public string TimeZone { get; set; }
    }
}
