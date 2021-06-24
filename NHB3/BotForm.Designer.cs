namespace NHB3
{
    partial class BotForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
			this.checkBox1 = new System.Windows.Forms.CheckBox();
			this.checkBox2 = new System.Windows.Forms.CheckBox();
			this.checkBox3 = new System.Windows.Forms.CheckBox();
			this.button1 = new System.Windows.Forms.Button();
			this.newLimitLbl = new System.Windows.Forms.Label();
			this.newLimitTxtBox = new System.Windows.Forms.TextBox();
			this.jsonSettingsLabel = new System.Windows.Forms.Label();
			this.jsonSettingsTextBox = new System.Windows.Forms.TextBox();
			this.SuspendLayout();
			// 
			// checkBox1
			// 
			this.checkBox1.AutoSize = true;
			this.checkBox1.Location = new System.Drawing.Point(9, 8);
			this.checkBox1.Margin = new System.Windows.Forms.Padding(2, 2, 2, 2);
			this.checkBox1.Name = "checkBox1";
			this.checkBox1.Size = new System.Drawing.Size(171, 17);
			this.checkBox1.TabIndex = 0;
			this.checkBox1.Text = "Refill order when spent hit 90%";
			this.checkBox1.UseVisualStyleBackColor = true;
			// 
			// checkBox2
			// 
			this.checkBox2.AutoSize = true;
			this.checkBox2.Location = new System.Drawing.Point(9, 29);
			this.checkBox2.Margin = new System.Windows.Forms.Padding(2, 2, 2, 2);
			this.checkBox2.Name = "checkBox2";
			this.checkBox2.Size = new System.Drawing.Size(317, 17);
			this.checkBox2.TabIndex = 1;
			this.checkBox2.Text = "Lower order price for one step if cheaper hashpower available";
			this.checkBox2.UseVisualStyleBackColor = true;
			// 
			// checkBox3
			// 
			this.checkBox3.AutoSize = true;
			this.checkBox3.Location = new System.Drawing.Point(9, 49);
			this.checkBox3.Margin = new System.Windows.Forms.Padding(2, 2, 2, 2);
			this.checkBox3.Name = "checkBox3";
			this.checkBox3.Size = new System.Drawing.Size(295, 17);
			this.checkBox3.TabIndex = 2;
			this.checkBox3.Text = "Increase order price for one step if order run out of miners";
			this.checkBox3.UseVisualStyleBackColor = true;
			// 
			// button1
			// 
			this.button1.Location = new System.Drawing.Point(327, 134);
			this.button1.Margin = new System.Windows.Forms.Padding(2, 2, 2, 2);
			this.button1.Name = "button1";
			this.button1.Size = new System.Drawing.Size(50, 21);
			this.button1.TabIndex = 3;
			this.button1.Text = "Save";
			this.button1.UseVisualStyleBackColor = true;
			this.button1.Click += new System.EventHandler(this.button1_Click);
			// 
			// newLimitLbl
			// 
			this.newLimitLbl.AutoSize = true;
			this.newLimitLbl.Location = new System.Drawing.Point(9, 72);
			this.newLimitLbl.Name = "newLimitLbl";
			this.newLimitLbl.Size = new System.Drawing.Size(95, 13);
			this.newLimitLbl.TabIndex = 4;
			this.newLimitLbl.Text = "Повышать limit на";
			// 
			// newLimitTxtBox
			// 
			this.newLimitTxtBox.Location = new System.Drawing.Point(150, 69);
			this.newLimitTxtBox.Name = "newLimitTxtBox";
			this.newLimitTxtBox.Size = new System.Drawing.Size(154, 20);
			this.newLimitTxtBox.TabIndex = 5;
			this.newLimitTxtBox.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.newLimitTxtBox_KeyPress);
			// 
			// jsonSettingsLabel
			// 
			this.jsonSettingsLabel.AutoSize = true;
			this.jsonSettingsLabel.Location = new System.Drawing.Point(9, 99);
			this.jsonSettingsLabel.Name = "jsonSettingsLabel";
			this.jsonSettingsLabel.Size = new System.Drawing.Size(110, 13);
			this.jsonSettingsLabel.TabIndex = 6;
			this.jsonSettingsLabel.Text = "URL JSON-настроек";
			// 
			// jsonSettingsTextBox
			// 
			this.jsonSettingsTextBox.Location = new System.Drawing.Point(150, 96);
			this.jsonSettingsTextBox.Name = "jsonSettingsTextBox";
			this.jsonSettingsTextBox.Size = new System.Drawing.Size(154, 20);
			this.jsonSettingsTextBox.TabIndex = 7;
			// 
			// BotForm
			// 
			this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
			this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
			this.ClientSize = new System.Drawing.Size(387, 163);
			this.Controls.Add(this.jsonSettingsTextBox);
			this.Controls.Add(this.jsonSettingsLabel);
			this.Controls.Add(this.newLimitTxtBox);
			this.Controls.Add(this.newLimitLbl);
			this.Controls.Add(this.button1);
			this.Controls.Add(this.checkBox3);
			this.Controls.Add(this.checkBox2);
			this.Controls.Add(this.checkBox1);
			this.Margin = new System.Windows.Forms.Padding(2, 2, 2, 2);
			this.Name = "BotForm";
			this.Text = "Bot settings";
			this.ResumeLayout(false);
			this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.CheckBox checkBox1;
        private System.Windows.Forms.CheckBox checkBox2;
        private System.Windows.Forms.CheckBox checkBox3;
        private System.Windows.Forms.Button button1;
		private System.Windows.Forms.Label newLimitLbl;
		private System.Windows.Forms.TextBox newLimitTxtBox;
		private System.Windows.Forms.Label jsonSettingsLabel;
		private System.Windows.Forms.TextBox jsonSettingsTextBox;
	}
}