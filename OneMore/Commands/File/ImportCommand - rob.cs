//************************************************************************************************
// Copyright © 2020 Steven M Cohn.  All rights reserved.
//************************************************************************************************

namespace River.OneMoreAddIn.Commands
{
	using Microsoft.Office.Interop.Outlook;
	using Newtonsoft.Json.Linq;
	using River.OneMoreAddIn.Helpers.Office;
	using River.OneMoreAddIn.Models;
	using River.OneMoreAddIn.UI;
	using System;
	using System.Drawing;
	using System.IO;
	using System.Text;
	using System.Text.RegularExpressions;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Web;
	using System.Windows.Forms;
	using System.Xml.Linq;


	internal class ImportCommand : Command
	{
		private const int MaxTimeout = 15;
		private UI.ProgressDialog progress;


		public ImportCommand()
		{
		}


		public override async Task Execute(params object[] args)
		{
			using var dialog = new ImportDialog();

			if (dialog.ShowDialog() != DialogResult.OK)
			{
				return;
			}

			switch (dialog.Format)
			{
				case ImportDialog.Formats.Word:
					ImportWord(dialog.FilePath, dialog.AppendToPage);
					break;

				case ImportDialog.Formats.PowerPoint:
					ImportPowerPoint(dialog.FilePath, dialog.AppendToPage, dialog.CreateSection);
					break;

				case ImportDialog.Formats.Xml:
					await ImportXml(dialog.FilePath);
					break;

				case ImportDialog.Formats.OneNote:
					await ImportOneNote(dialog.FilePath);
					break;

				case ImportDialog.Formats.Markdown:
					await ImportMarkdown(dialog.FilePath);
					break;
			}
		}


		/// <summary>
		/// Presents the ProgressDialog and invokes the given action. The work can be cancelled
		/// by the user or when a specified timeout expires.
		/// </summary>
		/// <param name="timeout">The time is seconds before the work is cancelled</param>
		/// <param name="path">The file path to action</param>
		/// <param name="action">The action to execute</param>
		/// <returns></returns>
		private bool RunWithProgress(int timeout, string path, Func<CancellationToken, Task<bool>> action)
		{
			using (progress = new ProgressDialog(timeout))
			{
				progress.SetMessage($"Importing {path}...");

				var result = progress.ShowTimedDialog(
					async (ProgressDialog progDialog, CancellationToken token) =>
					{
						try
						{
							await action(token);
						}
						catch (System.Exception exc)
						{
							logger.WriteLine("error importing", exc);
							return false;
						}
						await Task.Yield();
						return !token.IsCancellationRequested;
					});

				return result == DialogResult.OK;
			}
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// Word...

		private void ImportWord(string filepath, bool append)
		{
			if (!Office.IsInstalled("Word"))
			{
				UIHelper.ShowMessage("Word is not installed");
			}

			string[] files;
			int timeout = MaxTimeout;

			if (PathHelper.HasWildFileName(filepath))
			{
				files = Directory.GetFiles(Path.GetDirectoryName(filepath), Path.GetFileName(filepath));
				timeout = 10 + (files.Length * 4);
			}
			else
			{
				files = new string[] { filepath };
			}

			logger.StartClock();

			var completed = RunWithProgress(timeout, filepath, async (token) =>
			{
				foreach (var file in files)
				{
					if (token.IsCancellationRequested)
					{
						break;
					}

					await ImportWordFile(file, append, token);
				}

				return !token.IsCancellationRequested;
			});

			if (completed)
			{
				logger.WriteTime("word file(s) imported");
			}
			else
			{
				logger.WriteTime("word file(s) cancelled");
			}
		}


		private async Task ImportWordFile(string filepath, bool append, CancellationToken token)
		{
			progress.SetMessage($"Importing {filepath}...");

			using var word = new Word();

			//System.Diagnostics.Debugger.Launch();

			var html = word.ConvertFileToHtml(filepath);

			if (token.IsCancellationRequested)
			{
				logger.WriteLine("WordImporter cancelled");
				return;
			}

			if (append)
			{
				using var one = new OneNote(out var page, out _);
				page.AddHtmlContent(html);
				await one.Update(page);
			}
			else
			{
				using var one = new OneNote();
				one.CreatePage(one.CurrentSectionId, out var pageId);
				var page = one.GetPage(pageId);

				page.Title = Path.GetFileName(filepath);
				page.AddHtmlContent(html);
				await one.Update(page);
				await one.NavigateTo(page.PageId);
			}
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// Powerpoint...

		private void ImportPowerPoint(string filepath, bool append, bool split)
		{
			if (!Office.IsInstalled("Powerpoint"))
			{
				UIHelper.ShowMessage("PowerPoint is not installed");
			}

			string[] files;
			int timeout = MaxTimeout;

			if (PathHelper.HasWildFileName(filepath))
			{
				files = Directory.GetFiles(Path.GetDirectoryName(filepath), Path.GetFileName(filepath));
				timeout = 10 + (files.Length * 4);
			}
			else
			{
				files = new string[] { filepath };
			}

			logger.StartClock();

			var completed = RunWithProgress(timeout, filepath, async (token) =>
			{
				foreach (var file in files)
				{
					if (token.IsCancellationRequested)
					{
						break;
					}

					await ImportPowerPointFile(filepath, append, split, token);
				}

				return !token.IsCancellationRequested;
			});

			if (completed)
			{
				logger.WriteTime("powerpoint file(s) imported");
			}
			else
			{
				logger.WriteTime("powerpoint file(s) cancelled");
			}
		}


		private async Task ImportPowerPointFile(
			string filepath, bool append, bool split, CancellationToken token)
		{
			progress.SetMessage($"Importing {filepath}...");

			using var powerpoint = new PowerPoint();
			var outpath = powerpoint.ConvertFileToImages(filepath);

			if (outpath == null)
			{
				logger.WriteLine($"failed to create output path {filepath}");
				return;
			}

			if (token.IsCancellationRequested)
			{
				logger.WriteLine("PowerPointImporter cancelled");
				return;
			}

			if (split)
			{
				using var one = new OneNote();
				var section = await one.CreateSection(Path.GetFileNameWithoutExtension(filepath));
				var sectionId = section.Attribute("ID").Value;
				var ns = one.GetNamespace(section);

				await one.NavigateTo(sectionId);

				int i = 1;
				foreach (var file in Directory.GetFiles(outpath, "*.jpg"))
				{
					one.CreatePage(sectionId, out var pageId);
					var page = one.GetPage(pageId);
					page.Title = $"Slide {i}";
					var container = page.EnsureContentContainer();

					EmbedImage(container, ns, file);

					await one.Update(page);

					i++;
				}

				logger.WriteLine("created section");
			}
			else
			{
				using var one = new OneNote();
				Page page;
				if (append)
				{
					page = one.GetPage();
				}
				else
				{
					one.CreatePage(one.CurrentSectionId, out var pageId);
					page = one.GetPage(pageId);
					page.Title = Path.GetFileName(filepath);
				}

				var container = page.EnsureContentContainer();

				foreach (var file in Directory.GetFiles(outpath, "*.jpg"))
				{
					using var image = Image.FromFile(file);
					EmbedImage(container, page.Namespace, file);
				}

				await one.Update(page);

				if (!append)
				{
					await one.NavigateTo(page.PageId);
				}
			}

			try
			{
				Directory.Delete(outpath, true);
			}
			catch (System.Exception exc)
			{
				logger.WriteLine($"error cleaning up {outpath}", exc);
			}
		}


		private void EmbedImage(XElement container, XNamespace ns, string filepath)
		{
			using var image = Image.FromFile(filepath);

			container.Add(
				new XElement(ns + "OE",
					new XElement(ns + "Image",
						new XElement(ns + "Size",
							new XAttribute("width", $"{image.Width:00}"),
							new XAttribute("height", $"{image.Height:00}"),
							new XAttribute("isSetByUser", "true")),
						new XElement(ns + "Data", image.ToBase64String())
					)),
				new XElement(ns + "OE",
					new XElement(ns + "T", new XCData(string.Empty))
					)
				);
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// Markdown...

		private async Task ImportMarkdown(string filepath)
		{
			// TODO: This is where a single markdown file is processed.
			if (!PathHelper.HasWildFileName(filepath))
			{
				await ImportMarkdownFile(filepath, default);
				logger.WriteTime("markdown file imported");
				return;
			}

			// TODO: This is where multiple markdown files are processed.
			var files = Directory.GetFiles(Path.GetDirectoryName(filepath), Path.GetFileName(filepath));
			var timeout = 10 + (files.Length * 3);

			logger.StartClock();

			//var completed = RunWithProgress(timeout, filepath, async (token) =>
			//{
				foreach (var file in files)
				{
					//if (token.IsCancellationRequested)
					//{
					//	break;
					//}

					//System.Diagnostics.Debugger.Launch();
					//await ImportMarkdownFile(file, token);
					await ImportMarkdownFile(file, default);
			}

			//	return !token.IsCancellationRequested;
			//});

			//if (completed)
			//{
			//	logger.WriteTime("markdown file(s) imported");
			//}
			//else
			//{
			//	logger.WriteTime("markdown file(s) import cancelled");
			//}
		}


		private async Task ImportMarkdownFile(string filepath, CancellationToken token)
		{
			try
			{
				progress?.SetMessage($"Importing {filepath}...");
				//System.Diagnostics.Debugger.Launch();

				logger.WriteLine($"importing markdown {filepath}");
				var text = File.ReadAllText(filepath);

				// Parse the Created DateTime from the Obsidian markdown file.
				Regex dateCreatedRegex = new Regex(@".+Created.+: (.+)", RegexOptions.Compiled);
				var dateCreatedMatches = dateCreatedRegex.Matches(text);

				var dateCreatedString = string.Empty;
				var parsedDateCreatedString = false;
				DateTime dateCreated = DateTime.MinValue;

				string[] parsedText;

				if (dateCreatedMatches.Count > 0)
				{
					dateCreatedString = dateCreatedRegex.Matches(text)?[0]?.Groups[1]?.Value;
					parsedDateCreatedString = DateTime.TryParse(dateCreatedString, out dateCreated);

					// Strip out the Created date from the rest of the file.
					parsedText = dateCreatedRegex.Split(text);
					text = parsedText[parsedText.Length - 1];
				}

				// Parse the Modified DateTime from the Obsidian markdown file.
				Regex dateModifiedRegex = new Regex(@".+Modified.+: (.+)", RegexOptions.Compiled);
				var dateModifiedMatches = dateModifiedRegex.Matches(text);

				var dateModifiedString = string.Empty;
				var parsedDateModifiedString = false;
				DateTime dateModified = DateTime.MinValue;

				if (dateModifiedMatches.Count > 0)
				{
					dateModifiedString = dateModifiedRegex.Matches(text)?[0]?.Groups[1]?.Value;
					parsedDateModifiedString = DateTime.TryParse(dateModifiedString, out dateModified);

					// Strip out the Modified date from the rest of the file.
					parsedText = dateModifiedRegex.Split(text);
					text = parsedText[parsedText.Length - 1];
				}

				const string YellowHighlightRegexPattern = "<mark class=\"yellow\">(.*)</mark>";
				const string GreenHighlightRegexPattern = "<mark class=\"green\">(.*)</mark>";
				const string ImageRegexPattern1 = "!\\[(.*)\\]\\(/_attachments/(.*)\\)";
				const string ImageRegexPattern2 = "!\\[\\[(.*)\\]\\]";

				foreach (Match match in Regex.Matches(text, ImageRegexPattern2))
				{
					var matchToModify = match.Groups[1].Value;
					var modifiedMatch = matchToModify.Replace(" ", "%20");

					text = text.Replace(matchToModify, modifiedMatch);
				}

				text = Regex.Replace(text, YellowHighlightRegexPattern, "<p style=\"color:black;background:yellow;mso-highlight:yellow;\">$1</p>");
				text = Regex.Replace(text, GreenHighlightRegexPattern, "<p style=\"color:black;background:lime;mso-highlight:lime;\">$1</p>");
				text = Regex.Replace(text, ImageRegexPattern1, "![$1](file:///C:/Users/rob/OneDrive/Obsidian/Rob's%20Programming%20Notes/_attachments/$2)");
				text = Regex.Replace(text, ImageRegexPattern2, "![](file:///C:/Users/rob/OneDrive/Obsidian/Rob's%20Programming%20Notes/_attachments/$1)");


				if (token != default && token.IsCancellationRequested)
				{
					logger.WriteLine("import markdown cancelled");
					return;
				}

				// render HTML...

				var body = OneMoreDig.ConvertMarkdownToHtml(filepath, text);

				// copy/paste HTML...

				if (!string.IsNullOrEmpty(body))
				{
					var builder = new StringBuilder();
					builder.AppendLine("<html>");
					builder.AppendLine("<body>");
					builder.AppendLine("<!--StartFragment-->");
					builder.AppendLine(body);
					builder.AppendLine("<!--EndFragment-->");
					builder.AppendLine("</body>");
					builder.AppendLine("</html>");
					var html = PasteRtfCommand.AddHtmlPreamble(builder.ToString());

					if (token != default && token.IsCancellationRequested)
					{
						logger.WriteLine("import markdown cancelled");
						return;
					}

					var clippy = new ClipboardProvider();
					await clippy.StashState();
					await clippy.SetHtml(html);

					Page page;
					string pageId;

					using (var one = new OneNote())
					{
						one.CreatePage(one.CurrentSectionId, out pageId);

						page = one.GetPage(pageId, OneNote.PageDetail.Basic);
						page.Title = Path.GetFileNameWithoutExtension(filepath);

						// Set the page's Created DateTime.
						if (!string.IsNullOrEmpty(dateCreatedString) && parsedDateCreatedString)
						{
							// Set it to the parsed DateTime from the markdown file.
							page.SetCreatedDateTime(dateCreated.ToString("o"));
						}
						else
						{
							var creationTime = File.GetCreationTime(filepath);
							var modifiedTime = File.GetLastWriteTime(filepath);

							// Set the page's Created DateTime to the created or modified DateTime stamp from the
							// markdown file's properties, whichever is earlier.
							DateTime timeToUse = creationTime > modifiedTime ? modifiedTime : creationTime;
							page.SetCreatedDateTime(timeToUse.ToString("o"));
						}

						// Set the page's Modified DateTime.
						// TODO: Is it worth updating this since it immediately gets overwritten with today's DateTime?
						//                  if (!string.IsNullOrEmpty(dateModifiedString) && parsedDateModifiedString)
						//{
						//	page.SetModifiedDateTime(dateModified.ToString("o"));
						//}

						// TODO: Continue here!
						//XElement tagDefDefinition = new(page.Namespace + "TagDef");
	  //                  XElement tagDefRememberForLater = new(page.Namespace + "TagDef");

	  //                  XAttribute indexAttribute0 = new XAttribute("index", "0");
	  //                  XAttribute typeAttribute4 = new XAttribute("type", "4");
	  //                  XAttribute symbolAttribute0 = new XAttribute("symbol", "0");
						//XAttribute fontColorAtttribute0 = new XAttribute("fontColor", "#000000");
	  //                  XAttribute highlightColorGreenAtttribute = new XAttribute("highlightColor", "#00FF00");
	  //                  XAttribute definitionNameAtttribute = new XAttribute("name", "Definition");
	  //                  XAttribute rememberForLaterNameAtttribute = new XAttribute("name", "Remember for later");

	  //                  XAttribute indexAttribute1 = new XAttribute("index", "1");
	  //                  XAttribute typeAttribute3 = new XAttribute("type", "3");
	  //                  XAttribute highlightColorYellowAtttribute = new XAttribute("highlightColor", "#FFFF00");

	  //                  tagDefDefinition.Add(indexAttribute0);
	  //                  tagDefDefinition.Add(typeAttribute4);
	  //                  tagDefDefinition.Add(symbolAttribute0);
	  //                  tagDefDefinition.Add(fontColorAtttribute0);
	  //                  tagDefDefinition.Add(highlightColorGreenAtttribute);
	  //                  tagDefDefinition.Add(definitionNameAtttribute);

						//tagDefRememberForLater.Add(indexAttribute1);
	  //                  tagDefRememberForLater.Add(typeAttribute3);
	  //                  tagDefRememberForLater.Add(symbolAttribute0);
	  //                  tagDefRememberForLater.Add(fontColorAtttribute0);
	  //                  tagDefRememberForLater.Add(highlightColorYellowAtttribute);
	  //                  tagDefRememberForLater.Add(rememberForLaterNameAtttribute);

	  //                  page.AddQuickStyleDef(tagDefDefinition);
						//page.AddQuickStyleDef(tagDefRememberForLater);
						//TagDef tagDef = new TagDef();
						//page.AddTagDef()

						await one.Update(page);
						await one.NavigateTo(pageId);
					}

					await clippy.Paste(true);
					await clippy.RestoreState();

					using (var one = new OneNote())
					{
						page = one.GetPage(pageId, OneNote.PageDetail.Basic);
						MarkdownConverter.RewriteHeadings(page);
						await one.Update(page);
					}
				}
			}
			catch (System.Exception exc)
			{
				logger.WriteLine(exc);
				MoreMessageBox.ShowErrorWithLogLink(
					owner, "Could not import. See log file for details");
			}
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// XML...

		private async Task ImportXml(string filepath)
		{
			try
			{
				// load page-from-file
				var template = new Page(XElement.Load(filepath));

				using var one = new OneNote();
				one.CreatePage(one.CurrentSectionId, out var pageId);

				// remove any objectID values and let OneNote generate new IDs
				template.Root.Descendants().Attributes("objectID").Remove();

				// set the page ID to the new page's ID
				template.Root.Attribute("ID").Value = pageId;

				if (string.IsNullOrEmpty(template.Title))
				{
					template.Title = Path.GetFileNameWithoutExtension(filepath);
				}

				await one.Update(template);
				await one.NavigateTo(pageId);
			}
			catch (System.Exception exc)
			{
				logger.WriteLine(exc);
				MoreMessageBox.ShowErrorWithLogLink(
					owner, "Could not import. See log file for details");
			}
		}


		// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
		// OneNote...

		private async Task ImportOneNote(string filepath)
		{
			try
			{
				using var one = new OneNote();
				var pageId = await one.Import(filepath);

				if (!string.IsNullOrEmpty(pageId))
				{
					await one.NavigateTo(pageId);
				}
			}
			catch (System.Exception exc)
			{
				logger.WriteLine(exc);
				MoreMessageBox.ShowErrorWithLogLink(
					owner, "Could not import. See log file for details");
			}
		}
	}
}
