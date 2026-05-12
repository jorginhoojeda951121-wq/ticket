using System;
using System.Collections.Generic;
using OpenQA.Selenium;

namespace IRCTCTatkalBot.Helpers
{
    /// <summary>
    /// Helper class for working with HTML select elements.
    /// This is a lightweight alternative to Selenium's SelectElement.
    /// </summary>
    public class SelectHelper
    {
        private readonly IWebElement _selectElement;

        public SelectHelper(IWebElement selectElement)
        {
            if (selectElement.TagName != "select")
                throw new ArgumentException("Element must be a SELECT element");
            _selectElement = selectElement;
        }

        /// <summary>Selects option by visible text.</summary>
        public void SelectByText(string text)
        {
            var options = _selectElement.FindElements(By.TagName("option"));
            foreach (var option in options)
            {
                if (option.Text.Trim() == text.Trim())
                {
                    option.Click();
                    return;
                }
            }
            throw new NoSuchElementException($"No option with text '{text}' found");
        }

        /// <summary>Selects option by value attribute.</summary>
        public void SelectByValue(string value)
        {
            var options = _selectElement.FindElements(By.TagName("option"));
            foreach (var option in options)
            {
                string? optionValue = option.GetDomAttribute("value") ?? option.GetDomProperty("value");
                if (optionValue == value)
                {
                    option.Click();
                    return;
                }
            }
            throw new NoSuchElementException($"No option with value '{value}' found");
        }

        /// <summary>Selects option by index (0-based).</summary>
        public void SelectByIndex(int index)
        {
            var options = _selectElement.FindElements(By.TagName("option"));
            if (index < 0 || index >= options.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            options[index].Click();
        }

        /// <summary>Gets the currently selected option text.</summary>
        public string GetSelectedText()
        {
            var options = _selectElement.FindElements(By.TagName("option"));
            foreach (var option in options)
            {
                if (option.Selected)
                    return option.Text;
            }
            return string.Empty;
        }

        /// <summary>Gets all available options.</summary>
        public IReadOnlyList<IWebElement> GetOptions()
        {
            return _selectElement.FindElements(By.TagName("option")).AsReadOnly();
        }
    }
}
